Unicode true
RequestExecutionLevel user
!include "MUI2.nsh"
!include "x64.nsh"
Name "桌屿"
OutFile "${OUTPUT_EXE}"
InstallDir "$LOCALAPPDATA\Programs\Deskfold"
Icon "..\src\Deskfold.ico"
SetCompressor /SOLID lzma
ShowInstDetails nevershow
!define APPKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Deskfold"
!define MENUKEY "Software\Classes\DesktopBackground\Shell\Deskfold.Organize"
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Atrium.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动桌屿"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_ICONSTOP "桌屿需要 64 位 Windows。"
        Abort
    ${EndIf}
FunctionEnd

!macro StopInstalled
    IfFileExists "$INSTDIR\Atrium.exe" 0 done
    ExecWait '"$INSTDIR\Atrium.exe" --exit' $0
    IntCmp $0 0 done
    MessageBox MB_ICONSTOP "请先退出正在运行的桌屿，再重试。"
    Abort
    done:
!macroend

Section
    SetRegView 64
    !insertmacro StopInstalled
    SetOutPath "$INSTDIR"
    File /r /x "Uninstall.ps1" "${APP_DIR}\*.*"
    WriteUninstaller "$INSTDIR\Uninstall-Deskfold.exe"
    CreateShortcut "$SMPROGRAMS\桌屿.lnk" "$INSTDIR\Atrium.exe"
    WriteRegStr HKCU "${MENUKEY}" "" "一键整理桌面"
    WriteRegStr HKCU "${MENUKEY}" "Icon" '"$INSTDIR\Atrium.exe",0'
    WriteRegStr HKCU "${MENUKEY}\command" "" '"$INSTDIR\Atrium.exe" --organize'
    WriteRegStr HKCU "${APPKEY}" "DisplayName" "桌屿"
    WriteRegStr HKCU "${APPKEY}" "DisplayVersion" "1.1.0"
    WriteRegStr HKCU "${APPKEY}" "DisplayIcon" "$INSTDIR\Atrium.exe"
    WriteRegStr HKCU "${APPKEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${APPKEY}" "UninstallString" '"$INSTDIR\Uninstall-Deskfold.exe"'
SectionEnd

Section "Uninstall"
    SetRegView 64
    !insertmacro StopInstalled
    ReadRegStr $0 HKCU "${MENUKEY}\command" ""
    StrCmp $0 '"$INSTDIR\Atrium.exe" --organize' 0 +2
    DeleteRegKey HKCU "${MENUKEY}"
    Delete "$SMPROGRAMS\桌屿.lnk"
    DeleteRegKey HKCU "${APPKEY}"
    !include "${REMOVE_LIST}"
    Delete "$INSTDIR\Uninstall-Deskfold.exe"
    RMDir "$INSTDIR"
SectionEnd
