using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace Deskfold
{
    public sealed class InboxAccount
    {
        public string Address { get; set; }
        public string Provider { get; set; }
        public string Secret { get; set; }
        public uint Validity { get; set; }
        public uint LastUid { get; set; }
        public override string ToString() { return Address; }
    }

    partial class Board
    {
        readonly CancellationTokenSource mailLifetime = new CancellationTokenSource();
        readonly Dictionary<string,List<IMessageSummary>> inboxRows = new Dictionary<string,List<IMessageSummary>>();
        readonly Dictionary<string,string> inboxStatus = new Dictionary<string,string>();
        bool mailBusy;
        Window mailWindow, accountWindow;
        ComboBox accounts;
        ListBox messages;
        TextBlock mailStatus;

        static string ProtectCode(string text)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text),null,DataProtectionScope.CurrentUser));
        }
        static string MailError(Exception ex)
        {
            if(ex is MailKit.Security.AuthenticationException) return "登录失败，请确认已开启 IMAP 并使用邮箱授权码。";
            if(ex is CryptographicException) return "授权码无法解密，请重新连接此账号。";
            if(ex is OperationCanceledException) return "连接超时，请稍后刷新。";
            return "无法读取收件箱，请检查网络和 IMAP 服务。";
        }
        static async Task<ImapClient> ConnectInbox(InboxAccount account,CancellationToken token)
        {
            var client=new ImapClient {Timeout=15000};
            try {
                string host=account.Provider=="163" ? "imap.163.com" : "imap.qq.com";
                await client.ConnectAsync(host,993,SecureSocketOptions.SslOnConnect,token);
                if((client.Capabilities & ImapCapabilities.Id)!=0)
                    await client.IdentifyAsync(new ImapImplementation { Name="DeskIslet",Version="1.2",Vendor="DeskIslet" },token);
                var bytes=ProtectedData.Unprotect(Convert.FromBase64String(account.Secret),null,DataProtectionScope.CurrentUser);
                try { await client.AuthenticateAsync(account.Address,Encoding.UTF8.GetString(bytes),token); }
                finally {Array.Clear(bytes,0,bytes.Length);}
                await client.Inbox.OpenAsync(FolderAccess.ReadOnly,token);
                return client;
            } catch {client.Dispose();throw;}
        }
        async void RefreshInbox()
        {
            if(mailBusy || closed)return;
            mailBusy=true; RenderInbox();
            try {
                foreach(var account in prefs.Accounts.ToArray()) {
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(mailLifetime.Token)) {
                        timeout.CancelAfter(30000);
                        try {
                            using(var client=await ConnectInbox(account,timeout.Token)) {
                                var inbox=client.Inbox;
                                var rows=inbox.Count==0 ? new List<IMessageSummary>() :
                                    (await inbox.FetchAsync(Math.Max(0,inbox.Count-20),-1,
                                        MessageSummaryItems.UniqueId|MessageSummaryItems.Envelope|MessageSummaryItems.Flags,timeout.Token))
                                        .Where(m=>m.Envelope!=null && m.UniqueId.IsValid).OrderByDescending(m=>m.UniqueId.Id).ToList();
                                if(closed || !prefs.Accounts.Contains(account))continue;
                                uint newest=rows.Count>0 ? rows[0].UniqueId.Id : 0;
                                int fresh=NewUnread(account,inbox.UidValidity,rows);
                                bool changed=account.Validity!=inbox.UidValidity || newest>account.LastUid;
                                if(changed) {
                                    account.LastUid=account.Validity==inbox.UidValidity ? Math.Max(newest,account.LastUid) : newest;
                                    account.Validity=inbox.UidValidity; SavePreferences();
                                }
                                inboxRows[account.Address]=rows;
                                inboxStatus[account.Address]="最近同步 "+DateTime.Now.ToString("HH:mm")+" · 最近 "+rows.Count+" 封";
                                if(fresh>0)
                                    Notice("mail:"+account.Address,"收到新邮件",account.Address+" 有 "+fresh+" 封新未读邮件。","查看收件箱",
                                        ()=>{ShowInbox(); accounts.SelectedItem=account;},null);
                            }
                        } catch(Exception ex) { if(!closed && prefs.Accounts.Contains(account)) inboxStatus[account.Address]=MailError(ex); }
                    }
                }
            } finally {mailBusy=false; if(!closed) RenderInbox();}
        }
        internal static int NewUnread(InboxAccount account,uint validity,IEnumerable<IMessageSummary> rows)
        {
            if(account.Validity==0 || account.Validity!=validity)return 0;
            return rows.Count(m=>m.UniqueId.Id>account.LastUid && !(m.Flags??MessageFlags.None).HasFlag(MessageFlags.Seen));
        }
        void RenderInbox()
        {
            if(mailWindow==null)return;
            var account=accounts.SelectedItem as InboxAccount;
            messages.Items.Clear();
            if(account==null){mailStatus.Text="连接 QQ 或 163 邮箱后，每 2 分钟检查一次新邮件。";return;}
            List<IMessageSummary> rows; string status;
            mailStatus.Text=mailBusy ? "正在同步收件箱…" : inboxStatus.TryGetValue(account.Address,out status) ? status : "等待同步…";
            if(!inboxRows.TryGetValue(account.Address,out rows))return;
            foreach(var m in rows) {
                var label=((m.Flags??MessageFlags.None).HasFlag(MessageFlags.Seen)?"":"● ")+(m.Envelope.Subject??"（无主题）")+"\n"+m.Envelope.From;
                messages.Items.Add(new ListBoxItem {Content=label,Tag=m.UniqueId,Padding=new Thickness(4)});
            }
        }
        void ShowInbox()
        {
            if(mailWindow!=null){mailWindow.Activate();return;}
            StackPanel body;
            var dialog=Dialog("邮箱与新邮件通知",out body);
            mailWindow=dialog; dialog.Width=450; dialog.Closed += delegate {mailWindow=null;};
            accounts=new ComboBox {Margin=new Thickness(0,10,0,6),ItemsSource=prefs.Accounts.ToArray()};
            body.Children.Add(accounts);
            var row=new WrapPanel(); body.Children.Add(row);
            row.Children.Add(ActionButton("连接邮箱",ConfigureInbox)); row.Children.Add(ActionButton("刷新",RefreshInbox));
            row.Children.Add(ActionButton("移除账号",() => {
                var a=accounts.SelectedItem as InboxAccount; if(a==null)return;
                if(MessageBox.Show(dialog,"移除 "+a.Address+" 的本机连接？","桌屿",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                prefs.Accounts.Remove(a);
                try {SavePreferences();} catch(Exception ex){prefs.Accounts.Add(a);MessageBox.Show(ex.Message);return;}
                inboxRows.Remove(a.Address);inboxStatus.Remove(a.Address); ResetAccountList(null);
            }));
            mailStatus=Label("",12,"#D0D0D5"); body.Children.Add(mailStatus);
            messages=new ListBox {Height=230,Margin=new Thickness(0,8,0,0)};
            body.Children.Add(messages); messages.MouseDoubleClick += delegate {ReadMail();};
            body.Children.Add(ActionButton("阅读选中邮件",ReadMail));
            accounts.SelectionChanged += delegate {RenderInbox();};
            accounts.SelectedIndex=0; RenderInbox(); dialog.Show(); dialog.Activate();
            if(prefs.Accounts.Count>0) RefreshInbox();
        }
        void ResetAccountList(InboxAccount selected)
        {
            if(mailWindow==null)return;
            accounts.ItemsSource=prefs.Accounts.ToArray(); accounts.SelectedItem=selected??prefs.Accounts.FirstOrDefault(); RenderInbox();
        }
        void ConfigureInbox()
        {
            if(accountWindow!=null){accountWindow.Activate();return;}
            StackPanel body;
            var dialog=Dialog("连接邮箱",out body); accountWindow=dialog;
            dialog.Owner=mailWindow; dialog.Closed += delegate {accountWindow=null;};
            var service=new ComboBox {ItemsSource=new[]{"QQ 邮箱","163 邮箱"},SelectedIndex=0,Margin=new Thickness(0,10,0,6)};
            body.Children.Add(service); body.Children.Add(Label("邮箱地址",12,"#DDDDE2")); var address=Field(""); body.Children.Add(address);
            body.Children.Add(Label("IMAP 授权码",12,"#DDDDE2")); var code=new PasswordBox {Padding=new Thickness(6)}; body.Children.Add(code);
            var status=Label("请先在邮箱网页开启 IMAP 并生成授权码。",12,"#CACAD0"); body.Children.Add(status);
            var connect=new Button {Content="验证并连接",Margin=new Thickness(0,10,0,0)};body.Children.Add(connect);
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(mailLifetime.Token);
            dialog.Closed += delegate {cancel.Cancel();};
            connect.Click += async (s,e) => {
                var value=address.Text.Trim();
                try{if(!string.Equals(new MailAddress(value).Address,value,StringComparison.OrdinalIgnoreCase))throw new FormatException();}
                catch(FormatException){status.Text="请输入完整的邮箱地址。";return;}
                if(code.Password.Length==0){status.Text="请输入授权码。";return;}
                connect.IsEnabled=false; status.Text="正在验证连接…";
                try {
                    var a=new InboxAccount {Address=value,Provider=service.SelectedIndex==1?"163":"QQ",Secret=ProtectCode(code.Password)};
                    using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)) {
                        timeout.CancelAfter(25000); using(await ConnectInbox(a,timeout.Token)){}
                    }
                    if(cancel.IsCancellationRequested)return;
                    var old=prefs.Accounts.FirstOrDefault(x=>string.Equals(x.Address,value,StringComparison.OrdinalIgnoreCase));
                    if(old!=null){a.LastUid=old.LastUid;a.Validity=old.Validity;prefs.Accounts.Remove(old);}
                    prefs.Accounts.Add(a);
                    try{SavePreferences();}catch{prefs.Accounts.Remove(a);if(old!=null)prefs.Accounts.Add(old);throw;}
                    code.Clear(); dialog.Close(); ResetAccountList(a); nextMail=DateTime.MinValue; RefreshInbox();
                } catch(Exception ex){status.Text=MailError(ex);connect.IsEnabled=true;}
            };
            dialog.Show();
        }
        async void ReadMail()
        {
            var item=messages.SelectedItem as ListBoxItem; var account=accounts.SelectedItem as InboxAccount;
            if(item==null||account==null)return;
            var uid=(UniqueId)item.Tag; uint validity=account.Validity;
            StackPanel body; var dialog=Dialog("阅读邮件",out body); dialog.Width=520;
            var text=new TextBox {Text="正在读取…",IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=310,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,10,0,0)};body.Children.Add(text);
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(mailLifetime.Token)){
                timeout.CancelAfter(30000); EventHandler onClose=(s,e)=>timeout.Cancel(); dialog.Closed+=onClose; dialog.Show();
                try{
                    using(var client=await ConnectInbox(account,timeout.Token)){
                        if(client.Inbox.UidValidity!=validity){text.Text="收件箱已更新，请刷新后重试。";return;}
                        var summary=(await client.Inbox.FetchAsync(new[]{uid},MessageSummaryItems.BodyStructure,timeout.Token)).FirstOrDefault();
                        var part=summary==null?null:(summary.TextBody??summary.HtmlBody);
                        if(part==null||part.Octets>512*1024){text.Text="此邮件没有可预览的文本，或正文超过 512 KB。";return;}
                        var mime=await client.Inbox.GetBodyPartAsync(uid,part,timeout.Token) as TextPart;
                        if(mime==null){text.Text="没有可显示的正文。";return;}
                        text.Text=mime.IsHtml ? System.Net.WebUtility.HtmlDecode(Regex.Replace(mime.Text,"<[^>]{0,2048}>"," ")) : mime.Text;
                        var match=Regex.Match(text.Text,@"(?:验证码|校验码|verification code|security code)[^\d\r\n]{0,24}(\d{4,8})(?!\d)",RegexOptions.IgnoreCase);
                        if(match.Success) body.Children.Add(ActionButton("复制验证码",()=>{try{Clipboard.SetText(match.Groups[1].Value);}catch(ExternalException){MessageBox.Show("剪贴板正在使用，请重试。");}}));
                    }
                }catch(Exception ex){if(dialog.IsVisible)text.Text=MailError(ex);}
                finally{dialog.Closed-=onClose;}
            }
        }
    }
}
