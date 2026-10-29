using System.Net;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Administration;
using HackerOs.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>
/// Settings ▸ Users and the guest account (ADR 0187 sections 4 and 5): the guest sees the
/// "Create my account" banner, which asks the OS for its form, and no account is added beside it.
/// </summary>
public sealed class UsersPanelTests
{
    [Fact]
    public async Task The_guest_sees_the_create_my_account_banner_and_no_add_account_form()
    {
        FakeUserAccounts accounts = new(Account("guest", "Guest", isGuest: true, isCurrent: true));
        await using PanelHarness harness = await PanelHarness.RenderAsync(accounts);

        string html = harness.Html();
        Assert.Contains("You are using the guest account", html, StringComparison.Ordinal);
        Assert.Contains("Create my account", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Add an account", html, StringComparison.OrdinalIgnoreCase);

        await harness.RunAsync(panel => panel.CreateMyAccountAsync());

        Assert.Equal(1, accounts.GuestRequests);
    }

    [Fact]
    public async Task A_refused_request_tells_the_guest_to_use_the_sign_in_screen()
    {
        FakeUserAccounts accounts = new(Account("guest", "Guest", isGuest: true, isCurrent: true)) { AcceptGuestRequest = false };
        await using PanelHarness harness = await PanelHarness.RenderAsync(accounts);

        await harness.RunAsync(panel => panel.CreateMyAccountAsync());

        Assert.Contains("on the sign-in screen", harness.Html(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_administrator_sees_no_banner()
    {
        FakeUserAccounts accounts = new(Account("alice", "Alice", isGuest: false, isCurrent: true));
        await using PanelHarness harness = await PanelHarness.RenderAsync(accounts);

        string html = harness.Html();
        Assert.DoesNotContain("You are using the guest account", html, StringComparison.Ordinal);
        Assert.Contains("users.manage", html, StringComparison.Ordinal);
    }

    private static HackUserAccount Account(string login, string name, bool isGuest, bool isCurrent) =>
        new(login, name, AppAuthority.Administrator, true, !isGuest, DateTimeOffset.UnixEpoch, isCurrent) { IsGuest = isGuest };

    private sealed class FakeUserAccounts(params HackUserAccount[] accounts) : IAppUserAccountsGateway
    {
        public int GuestRequests { get; private set; }

        public bool AcceptGuestRequest { get; init; } = true;

        public ValueTask<IReadOnlyList<HackUserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<HackUserAccount>>(accounts);

        public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> RequestGuestAccountCreationAsync(CancellationToken cancellationToken = default)
        {
            GuestRequests++;
            return ValueTask.FromResult(AcceptGuestRequest);
        }
    }

    private sealed class PanelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly IAppExecutionContext _context;
        private HtmlRootComponent _root;

        private PanelHarness(ServiceProvider services, HtmlRenderer renderer, IAppExecutionContext context)
        {
            _services = services;
            _renderer = renderer;
            _context = context;
        }

        public UsersPanel Panel { get; private set; } = default!;

        public static async Task<PanelHarness> RenderAsync(IAppUserAccountsGateway accounts)
        {
            IAppExecutionContext context = new MinimalAppExecutionContext(
                AppDefinitionMapper.ToManifest(new SettingsApp()),
                userId: "guest",
                homeDirectory: "/home/guest",
                userAccounts: accounts);
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            PanelHarness harness = new(services, new HtmlRenderer(services, NullLoggerFactory.Instance), context);
            harness._root = await harness._renderer.Dispatcher.InvokeAsync(() => harness._renderer.RenderComponentAsync<PanelHost>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(PanelHost.Context)] = context,
                    [nameof(PanelHost.Captured)] = (Action<UsersPanel>)(panel => harness.Panel = panel)
                })));
            await harness._root.QuiescenceTask;
            return harness;
        }

        /// <summary>Runs a panel action on the renderer's dispatcher, inside the app's ambient scope (as a click does).</summary>
        public async Task RunAsync(Func<UsersPanel, Task> action)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using (HackExecutionContext.Enter(_context))
                {
                    await action(Panel);
                }
            });
        }

        public string Html() => WebUtility.HtmlDecode(_renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString()).GetAwaiter().GetResult());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = default!;

        [Parameter]
        public Action<UsersPanel> Captured { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<IAppExecutionContext>.Value), Context);
            builder.AddComponentParameter(2, nameof(CascadingValue<IAppExecutionContext>.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<UsersPanel>(0);
                child.AddComponentReferenceCapture(1, reference => Captured((UsersPanel)reference));
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
