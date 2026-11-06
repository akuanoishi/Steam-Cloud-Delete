// SteamCloudWipe - delete your own Steam Cloud files for any game, installed or not.
// Logs in like the Steam client (QR code) and uses Steam's internal Cloud service.
using System.Net;
using System.Text.RegularExpressions;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamCloudWipe;

record CloudApp(uint AppId, string Name, int Files, string Size)
{
    public override string ToString() => $"{Name}  ({AppId})  -  {Files} files, {Size}";
}

class SteamSession
{
    readonly SteamClient client = new();
    readonly SteamUser user;
    readonly Cloud cloud;
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<EResult> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool running = true;
    string accessToken = "";
    public string AccountName { get; private set; } = "";

    public SteamSession()
    {
        var mgr = new CallbackManager(client);
        user = client.GetHandler<SteamUser>()!;
        cloud = client.GetHandler<SteamUnifiedMessages>()!.CreateService<Cloud>();
        mgr.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        mgr.Subscribe<SteamClient.DisconnectedCallback>(_ => { connected.TrySetException(new Exception("Could not connect to Steam.")); loggedOn.TrySetResult(EResult.NoConnection); });
        mgr.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb.Result));
        new Thread(() => { while (running) mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }) { IsBackground = true }.Start();
    }

    public async Task ConnectAsync() { client.Connect(); await connected.Task; }

    public async Task<string> LoginWithQrAsync(Action<string> showQr)
    {
        var s = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails { DeviceFriendlyName = "SteamCloudWipe" });
        s.ChallengeURLChanged = () => showQr(s.ChallengeURL);
        showQr(s.ChallengeURL);
        var p = await s.PollingWaitForResultAsync();
        await LogOnAsync(p.AccountName, p.RefreshToken, p.AccessToken);
        return p.RefreshToken;
    }

    public async Task LogOnAsync(string account, string refreshToken, string? token = null)
    {
        user.LogOn(new SteamUser.LogOnDetails { Username = account, AccessToken = refreshToken });
        var r = await loggedOn.Task;
        if (r != EResult.OK) throw new Exception($"Steam login failed: {r}");
        AccountName = account;
        accessToken = token ?? (await client.Authentication.GenerateAccessTokenForAppAsync(client.SteamID!, refreshToken)).AccessToken;
    }

    // Steam's "Steam Cloud" account page lists every app with cloud files, including uninstalled/unowned ones.
    public async Task<List<CloudApp>> ListAppsAsync()
    {
        var cookies = new CookieContainer();
        cookies.Add(new Cookie("steamLoginSecure", $"{client.SteamID!.ConvertToUInt64()}%7C%7C{accessToken}", "/", "store.steampowered.com"));
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        var html = await http.GetStringAsync("https://store.steampowered.com/account/remotestorage?l=english");
        var apps = new List<CloudApp>();
        foreach (Match row in Regex.Matches(html, "(?s)<tr>(.*?)</tr>"))
        {
            var id = Regex.Match(row.Groups[1].Value, @"appid=(\d+)");
            if (!id.Success) continue;
            var cells = Regex.Matches(row.Groups[1].Value, "(?s)<td[^>]*>(.*?)</td>")
                .Select(m => WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).Trim()).ToList();
            if (cells.Count < 3) continue;
            apps.Add(new CloudApp(uint.Parse(id.Groups[1].Value), cells[0], int.TryParse(cells[1], out var n) ? n : 0, cells[2]));
        }
        if (apps.Count == 0 && !html.Contains("remotestorage")) throw new Exception("Could not read the Steam Cloud page (web login rejected).");
        return apps;
    }

    async Task<List<string>> ListFilesAsync(uint app)
    {
        var all = new List<string>();
        while (true)
        {
            var e = await cloud.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request { appid = app, count = 500, start_index = (uint)all.Count });
            if (e.Result != EResult.OK) throw new Exception($"Listing files failed: {e.Result}");
            all.AddRange(e.Body.files.Select(f => f.filename));
            if (e.Body.files.Count == 0 || all.Count >= e.Body.total_files) return all;
        }
    }

    // Same sequence the Steam client uses: begin batch -> delete each file -> complete batch. Returns files left.
    public async Task<int> DeleteAppAsync(uint app, Action<string> log)
    {
        var names = await ListFilesAsync(app);
        if (names.Count == 0) return 0;
        var req = new CCloud_BeginAppUploadBatch_Request { appid = app, machine_name = "SteamCloudWipe" };
        req.files_to_delete.AddRange(names);
        var batch = (await cloud.BeginAppUploadBatch(req)).Body.batch_id;

        using var gate = new SemaphoreSlim(16);
        int failed = 0;
        await Task.WhenAll(names.Select(async n =>
        {
            await gate.WaitAsync();
            try
            {
                var d = await cloud.ClientDeleteFile(new CCloud_ClientDeleteFile_Request { appid = app, filename = n, is_explicit_delete = true, upload_batch_id = batch });
                if (d.Result != EResult.OK) { Interlocked.Increment(ref failed); log($"   {d.Result}: {n}"); }
            }
            finally { gate.Release(); }
        }));
        await cloud.CompleteAppUploadBatchBlocking(new CCloud_CompleteAppUploadBatch_Request { appid = app, batch_id = batch, batch_eresult = (uint)(failed == 0 ? EResult.OK : EResult.Fail) });
        return (await ListFilesAsync(app)).Count;
    }

    public void Close() { try { user.LogOff(); Thread.Sleep(300); } catch { } running = false; client.Disconnect(); }
}

class MainForm : Form
{
    readonly SteamSession steam = new();
    readonly Label status = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8), Text = "Connecting to Steam..." };
    readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage };
    readonly CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true, Visible = false, IntegralHeight = false };
    readonly TextBox logBox = new() { Dock = DockStyle.Bottom, Height = 120, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    readonly Button selectAll = new() { Text = "Select all", Width = 110, Enabled = false };
    readonly Button delete = new() { Text = "Delete selected", Width = 130, Enabled = false };

    public MainForm()
    {
        Text = "SteamCloudWipe"; Width = 720; Height = 640;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };
        buttons.Controls.AddRange([selectAll, delete]);
        Controls.Add(list); Controls.Add(qr); Controls.Add(logBox); Controls.Add(buttons); Controls.Add(status);
        selectAll.Click += (_, _) => { bool all = list.CheckedItems.Count < list.Items.Count; for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, all); };
        delete.Click += async (_, _) => await DeleteSelected();
        Shown += async (_, _) => await Start();
        FormClosing += (_, _) => steam.Close();
    }

    void Log(string s) { if (InvokeRequired) { BeginInvoke(() => Log(s)); return; } logBox.AppendText(s + Environment.NewLine); }

    async Task Start()
    {
        try
        {
            await steam.ConnectAsync();
            status.Text = "Scan this QR code with the Steam mobile app (Steam Guard > Scan QR code).";
            await steam.LoginWithQrAsync(url => BeginInvoke(() => qr.Image = MakeQr(url)));
            qr.Visible = false; list.Visible = true;
            await Reload();
        }
        catch (Exception ex) { status.Text = "Error: " + ex.Message; }
    }

    async Task Reload()
    {
        status.Text = "Loading your Steam Cloud games...";
        var apps = await steam.ListAppsAsync();
        list.Items.Clear();
        foreach (var a in apps.OrderBy(a => a.Name)) list.Items.Add(a);
        status.Text = $"Logged in as {steam.AccountName}. {apps.Count} games have files in Steam Cloud.";
        selectAll.Enabled = delete.Enabled = apps.Count > 0;
    }

    async Task DeleteSelected()
    {
        var picked = list.CheckedItems.Cast<CloudApp>().ToList();
        if (picked.Count == 0) return;
        if (MessageBox.Show($"Permanently delete {picked.Sum(a => a.Files)} files from {picked.Count} games in Steam Cloud?\n\nThis cannot be undone.",
            "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        selectAll.Enabled = delete.Enabled = list.Enabled = false;
        foreach (var a in picked)
        {
            status.Text = $"Deleting {a.Name}...";
            try { var left = await steam.DeleteAppAsync(a.AppId, Log); Log(left == 0 ? $"OK    {a.Name}: all files deleted" : $"FAIL  {a.Name}: {left} files remain"); }
            catch (Exception ex) { Log($"FAIL  {a.Name}: {ex.Message}"); }
        }
        list.Enabled = true;
        await Reload();
        Log("Done. Tip: also delete local save folders of installed games, or Steam may re-upload them when you play.");
    }

    static Image MakeQr(string url)
    {
        using var gen = new QRCodeGenerator();
        var png = new PngByteQRCode(gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L)).GetGraphic(8);
        return Image.FromStream(new MemoryStream(png));
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Dev only: --selftest <file with "account\nrefreshToken"> lists cloud apps without the GUI.
        if (args.Length == 2 && args[0] == "--selftest")
        {
            var p = File.ReadAllText(args[1]).Trim().Split('\n', 2);
            var s = new SteamSession();
            s.ConnectAsync().GetAwaiter().GetResult();
            s.LogOnAsync(p[0].Trim(), p[1].Trim()).GetAwaiter().GetResult();
            var apps = s.ListAppsAsync().GetAwaiter().GetResult();
            Console.WriteLine($"apps={apps.Count}");
            foreach (var a in apps.Take(5)) Console.WriteLine(a);
            s.Close();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
