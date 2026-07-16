using System.Collections.ObjectModel; using System.ComponentModel; using System.Runtime.CompilerServices; using System.Windows; using System.Windows.Controls; using CodexAccountBar.Models; using CodexAccountBar.Services;
namespace CodexAccountBar;
public partial class MainWindow:Window,INotifyPropertyChanged
{
    private readonly AccountVault _vault=new();private readonly CodexService _codex=new();private readonly UsageService _usage=new();private readonly NineRouterService _router=new();private bool _routerRunning,_routerCanToggle,_routerBusy,_forceClose;private string _routerDetail="Detecting…",_message="";
    public ObservableCollection<AccountRecord> Accounts{get;}=[];public bool RouterRunning{get=>_routerRunning;private set=>Set(ref _routerRunning,value);}public bool RouterCanToggle=>_routerCanToggle&&!_routerBusy;public string RouterDetail{get=>_routerDetail;private set=>Set(ref _routerDetail,value);}public string Message{get=>_message;private set{Set(ref _message,value);Changed(nameof(MessageVisibility));}}public Visibility MessageVisibility=>string.IsNullOrWhiteSpace(Message)?Visibility.Collapsed:Visibility.Visible;public Visibility EmptyVisibility=>Accounts.Count==0?Visibility.Visible:Visibility.Collapsed;public event PropertyChangedEventHandler? PropertyChanged;
    public MainWindow(){InitializeComponent();DataContext=this;Loaded+=async(_,_)=>{if(Accounts.Count==0)await Load();};}
    private async Task Load(){try{RouterDetail="Restoring official Codex configuration…";try{await _router.EnsureStoppedAsync();await _codex.RestoreOfficialConfigAsync();}catch(Exception e){AppLog.Error("Restore official configuration",e);Message="Could not fully disable 9Router: "+e.Message;}Accounts.Clear();foreach(var a in await _vault.LoadAsync())Accounts.Add(a);var active=await _codex.ReadActiveAuthAsync();if(active is not null){var id=AuthInspector.Inspect(active);foreach(var a in Accounts)a.IsActive=a.Email.Equals(id.Email,StringComparison.OrdinalIgnoreCase);if(!Accounts.Any(x=>x.IsActive)){var added=await _vault.SaveAsync(active);added.IsActive=true;Accounts.Add(added);}}Changed(nameof(EmptyVisibility));await RefreshAllAsync();}catch(Exception e){Message=e.Message;}}
    public async Task RefreshAllAsync(){Message="";await RefreshRouter();foreach(var a in Accounts){try{var u=await _usage.FetchAsync(await _vault.ReadAuthAsync(a.Id));a.SessionUsed=u.SessionUsed;a.WeeklyUsed=u.WeeklyUsed;a.SessionReset=u.SessionReset;a.WeeklyReset=u.WeeklyReset;a.StatusText="Usage refreshed";}catch(Exception e){a.StatusText=e.Message;}a.NotifyAll();}}
    private async Task RefreshRouter(){var s=await _router.DetectAsync();RouterRunning=s.Running;_routerCanToggle=s.Installed;RouterDetail=s.Detail;Changed(nameof(RouterCanToggle));}
    private async void RouterToggle_Click(object s,RoutedEventArgs e){if(_routerBusy)return;var requested=(s as System.Windows.Controls.CheckBox)?.IsChecked==true;_routerBusy=true;Changed(nameof(RouterCanToggle));RouterDetail=requested?"Starting 9Router…":"Stopping 9Router…";try{var n=await _router.SetRunningAsync(requested);RouterRunning=n.Running;RouterDetail=n.Detail;}catch(Exception x){Message=x.Message;await RefreshRouter();}finally{_routerBusy=false;Changed(nameof(RouterCanToggle));}}
    private async void Add_Click(object s,RoutedEventArgs e)
    {
        Message="Complete Codex sign-in in your browser…";
        try
        {
            string auth;
            try { auth=await _codex.LoginIsolatedAsync(); }
            catch (FileNotFoundException)
            {
                auth=await _codex.ReadActiveAuthAsync() ?? throw new InvalidOperationException("Codex CLI is not installed and no active Codex account was found. Install the Codex CLI to add a new account.");
                Message="Codex CLI not found; importing the account currently signed in to Codex…";
            }
            var a=await _vault.SaveAsync(auth);if(!Accounts.Any(x=>x.Id==a.Id))Accounts.Add(a);Changed(nameof(EmptyVisibility));Message=$"Saved {a.Email} securely.";
        }
        catch(Exception x){Message=x.Message;}
    }
    private async void Switch_Click(object s,RoutedEventArgs e){if((s as System.Windows.Controls.Button)?.Tag is not AccountRecord a)return;Message=$"Switching to {a.Email}…";try{await _codex.WriteAndRestartAsync(await _vault.ReadAuthAsync(a.Id));foreach(var x in Accounts){x.IsActive=x.Id==a.Id;x.NotifyAll();}Message=$"Switched to {a.Email}. Codex Desktop restarted.";}catch(Exception x){Message=x.Message;}}
    private async void Remove_Click(object s,RoutedEventArgs e){if((s as System.Windows.Controls.Button)?.Tag is not AccountRecord a)return;if(System.Windows.MessageBox.Show($"Remove {a.Email} from this app?","Codex Account Bar",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;await _vault.RemoveAsync(a.Id);Accounts.Remove(a);Changed(nameof(EmptyVisibility));}
    private async void Refresh_Click(object s,RoutedEventArgs e){Message="Refreshing accounts and usage…";await RefreshAllAsync();Message="Refresh complete.";}private void Hide_Click(object s,RoutedEventArgs e)=>Hide();private void Settings_Click(object s,RoutedEventArgs e)=>Message=$"Codex auth: {_codex.AuthPath}\n9Router service, Docker and npm CLI detection enabled.";public void ForceClose(){_forceClose=true;Close();}
    protected override void OnClosing(CancelEventArgs e){if(!_forceClose){e.Cancel=true;Hide();}base.OnClosing(e);}private void Set<T>(ref T f,T v,[CallerMemberName]string? n=null){if(EqualityComparer<T>.Default.Equals(f,v))return;f=v;Changed(n);}private void Changed([CallerMemberName]string? n=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));
}
