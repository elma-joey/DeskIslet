$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'dist/Deskfold.exe'))
$fixture = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('deskfold-quota-' + [guid]::NewGuid().ToString('N')))).FullName
# A stdio peer exercises the same process, handshake, parser and cache as the real client.
$source = @"
using System;
class Peer {
    static void Main() {
        string mode = Environment.GetEnvironmentVariable("DESKFOLD_TEST_CASE"), line;
        while ((line = Console.ReadLine()) != null) {
            if (line.Contains("\"initialize\"")) Console.WriteLine("{\"id\":1,\"result\":{}}");
            else if (line.Contains("\"account/read\"")) {
                string who = mode == "switch" ? "b@example.test" : "a@example.test";
                string account = mode == "logout" ? "null" : "{\"type\":\"" + (mode == "api" ? "apiKey" : "chatgpt") + "\",\"email\":\"" + who + "\"}";
                Console.WriteLine("{\"id\":2,\"result\":{\"account\":" + account + "}}");
            } else if (line.Contains("account/rateLimits/read")) {
                if (mode == "offline" || mode == "switch") Console.WriteLine("{\"id\":3,\"error\":{\"code\":-1}}");
                else {
                    string first = mode == "one" ? "null" : "{\"usedPercent\":100,\"windowDurationMins\":300}";
                    string second = mode == "empty" ? "null" : "{\"usedPercent\":0,\"windowDurationMins\":10080,\"resetsAt\":1900000000}";
                    if (mode == "empty") first = "{\"resetsAt\":1900000000}";
                    Console.WriteLine("{\"id\":3,\"result\":{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":" + first + ",\"secondary\":" + second + "}}}}");
                }
            }
        }
    }
}
"@
Add-Type -TypeDefinition $source -OutputAssembly (Join-Path $fixture 'codex.exe') -OutputType ConsoleApplication
$oldPath = $env:PATH
$oldCase = $env:DESKFOLD_TEST_CASE
$statePath = Join-Path $fixture 'codex-quota.json'
function Read-Case([string]$case) {
    $env:DESKFOLD_TEST_CASE = $case
    $before = [IO.File]::GetLastWriteTimeUtc($statePath)
    $sync = [Deskfold.CodexQuota]::new($fixture)
    try {
        $sync.Refresh()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ([IO.File]::GetLastWriteTimeUtc($statePath) -eq $before -and $timer.Elapsed.TotalSeconds -lt 8) { Start-Sleep -Milliseconds 50 }
        if ([IO.File]::GetLastWriteTimeUtc($statePath) -eq $before) { throw "No publication for $case" }
        Get-Content $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
    } finally { $sync.Stop() }
}
try {
    $env:PATH = "$fixture;$oldPath"
    $s = Read-Case 'both'
    if ($s.Windows.Count -ne 2 -or $s.Windows[0].Remaining -ne 0 -or $s.Windows[0].Reset -ne '' -or $s.Windows[1].Remaining -ne 100) { throw 'Two-window parsing failed' }
    $s = Read-Case 'one'
    if ($s.Windows.Count -ne 1 -or $s.Windows[0].Label -ne '7 天' -or $s.Windows[0].Reset -ne [DateTimeOffset]::FromUnixTimeSeconds(1900000000).ToLocalTime().ToString('MM/dd HH:mm')) { throw 'Weekly window or local reset time incorrect' }
    $when = $s.Updated
    $s = Read-Case 'offline'
    if ($s.Windows.Count -ne 1 -or $s.Updated -ne $when -or $s.Status -notlike '更新失败*') { throw 'Cached failure state incorrect' }
    $s = Read-Case 'switch'
    if ($s.Windows.Count -ne 0) { throw 'Another account received cached data' }
    foreach ($case in 'logout','api','empty') {
        $s = Read-Case $case
        if ($s.Windows.Count -ne 0 -or $s.Status -eq '已同步') { throw "Incorrect empty status for $case" }
    }
    'PASS: two, one and missing windows, local reset time, offline cache, account switch, signed out and API key.'
} finally {
    $env:PATH = $oldPath
    $env:DESKFOLD_TEST_CASE = $oldCase
}
