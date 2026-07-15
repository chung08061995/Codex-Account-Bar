using System.Text; using System.Text.Json; using CodexAccountBar.Models;
namespace CodexAccountBar.Services;
public sealed class AccountVault
{
    private readonly string _root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexAccountBar"); private string Index=>Path.Combine(_root,"accounts.json");
    public AccountVault()=>Directory.CreateDirectory(_root);
    public async Task<IReadOnlyList<AccountRecord>> LoadAsync()=>(await Items()).Where(x=>File.Exists(Secret(x.Id))).Select(x=>new AccountRecord{Id=x.Id,Email=x.Email,Plan=x.Plan,AddedAt=x.AddedAt}).ToList();
    public async Task<string> ReadAuthAsync(string id)=>Encoding.UTF8.GetString(NativeDpapi.Unprotect(await File.ReadAllBytesAsync(Secret(id))));
    public async Task<AccountRecord> SaveAsync(string json)
    {
        var info=AuthInspector.Inspect(json); var current=(await LoadAsync()).FirstOrDefault(x=>x.Email.Equals(info.Email,StringComparison.OrdinalIgnoreCase));
        var account=current??new AccountRecord{Id=Guid.NewGuid().ToString("N"),Email=info.Email,Plan=info.Plan};
        await File.WriteAllBytesAsync(Secret(account.Id),NativeDpapi.Protect(Encoding.UTF8.GetBytes(json)));
        var all=(await Items()).Where(x=>x.Id!=account.Id).ToList(); all.Add(new(account.Id,account.Email,account.Plan,account.AddedAt)); await Write(all); return account;
    }
    public async Task RemoveAsync(string id){if(File.Exists(Secret(id)))File.Delete(Secret(id));await Write((await Items()).Where(x=>x.Id!=id).ToList());}
    private string Secret(string id)=>Path.Combine(_root,$"account-{id}.bin");
    private async Task<List<AccountIndexItem>> Items()=>!File.Exists(Index)?[]:JsonSerializer.Deserialize<List<AccountIndexItem>>(await File.ReadAllTextAsync(Index))??[];
    private async Task Write(List<AccountIndexItem> items){var temp=Index+".tmp";await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(items,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,Index,true);}
}
