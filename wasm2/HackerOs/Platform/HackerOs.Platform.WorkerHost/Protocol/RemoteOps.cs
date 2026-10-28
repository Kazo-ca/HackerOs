namespace HackerOs.Platform.WorkerHost.Protocol;

/// <summary>
/// Names of the remote gateway operations (<c>o</c> of a <c>call</c> or <c>note</c> message), one per
/// SDK gateway member a worker can reach (ADR 0070, section 4). The OS-side dispatcher serves each
/// one through the process's trusted context, with the same capability checks as in-process.
/// </summary>
internal static class RemoteOps
{
    // Filesystem (IAppFileSystemGateway): read(2) is a handle opened once, then read in chunks.
    public const string FsRead = "fs.read";
    public const string FsReadChunk = "fs.chunk";
    public const string FsReadClose = "fs.close";
    public const string FsEnumerate = "fs.enum";
    public const string FsCreate = "fs.create";
    public const string FsWrite = "fs.write";
    public const string FsMove = "fs.move";
    public const string FsCopy = "fs.copy";
    public const string FsDelete = "fs.delete";
    public const string FsStat = "fs.stat";
    public const string FsSetPermissions = "fs.chmod";

    // Standard input (pipe or terminal of the shell).
    public const string StdinRead = "in.read";

    // Process environment (IAppEnvironmentGateway): local copy in the worker, written through.
    public const string EnvSetDirectory = "env.cd";
    public const string EnvSetVariable = "env.set";

    // Logging, notifications: fire-and-forget.
    public const string Log = "log";
    public const string LogFile = "log.file";
    public const string LogSite = "log.site";
    public const string NotificationPost = "notify.post";

    // Clock.
    public const string ClockDelay = "clock.delay";

    // Processes (IAppProcessGateway).
    public const string ProcessStop = "proc.stop";
    public const string ProcessKill = "proc.kill";

    /// <summary>The process table (ADR 0131, section 4): only ever called synchronously, by <c>ListProcesses</c>.</summary>
    public const string ProcessList = "proc.list";

    // Network (IAppNetworkGateway).
    public const string NetResolve = "net.resolve";
    public const string NetReverse = "net.reverse";
    public const string NetPing = "net.ping";
    public const string NetHttp = "net.http";
    public const string NetTcp = "net.tcp";
    public const string NetOs = "net.os";

    // Listening sockets (ADR 0074, section 2): requests then travel as hreq/hres messages.
    public const string NetListen = "net.listen";
    public const string NetUnlisten = "net.unlisten";
    public const string NetListeners = "net.listeners";

    // App catalog, services, drives, intents, clipboard.
    public const string CatalogInstalled = "cat.installed";
    public const string CatalogHandlers = "cat.handlers";
    public const string CatalogDefaults = "cat.defaults";
    public const string CatalogSetDefault = "cat.setdef";
    public const string ServiceStart = "svc.start";
    public const string ServiceStop = "svc.stop";
    public const string ServiceGetMode = "svc.getmode";
    public const string ServiceSetMode = "svc.setmode";
    public const string ServiceStatus = "svc.status";
    public const string ServiceResetFailed = "svc.resetfailed";
    public const string ServiceNotify = "svc.notify";
    public const string Drives = "drv.list";
    public const string DriveHostSupported = "drv.hostsup";
    public const string DriveMount = "drv.mount";
    public const string DriveReconnect = "drv.reconnect";
    public const string DriveUnmount = "drv.umount";
    public const string CrossOriginIsolation = "coi.get";
    public const string IntentLaunch = "int.launch";
    public const string IntentOpenFile = "int.open";
    public const string IntentEditFile = "int.edit";
    public const string IntentReveal = "int.reveal";
    public const string IntentOpenUri = "int.uri";
    public const string IntentShowSettings = "int.settings";
    public const string IntentSend = "int.send";
    public const string ClipboardGetText = "clip.get";
    public const string ClipboardSetText = "clip.set";
    public const string ClipboardGetFiles = "clip.getfiles";
    public const string ClipboardSetFiles = "clip.setfiles";
    public const string ClipboardClear = "clip.clear";

    // Events and directory watches (ADR 0124): the bus stays in the OS; a worker subscribes by
    // message and receives each delivery as an `ev` message, acknowledged by `ev.ack` (credit).
    public const string EventSubscribe = "ev.sub";
    public const string EventUnsubscribe = "ev.unsub";
    public const string EventPublish = "ev.pub";

    /// <summary><c>PublishAsync</c> (ADR 0145): the same publication as a call, answered with the OS's outcome and faults.</summary>
    public const string EventPublishWithResult = "ev.pubr";
    public const string EventSharedChannel = "ev.chan";
    public const string EventAcknowledge = "ev.ack";
    public const string WatchOpen = "watch.open";

    // Background tasks (ADR 0128): a task's reports (note, from the task's own process), and a task
    // started by a process already in a worker (call, section 6; reports come back as `bgp` messages).
    public const string BackgroundProgress = "bg.progress";
    public const string BackgroundRun = "bg.run";

    // Browser database of a web site (IAppWebDatabaseGateway, ADR 0088): the schema or the batch as JSON.
    public const string WebDbOpen = "wdb.open";
    public const string WebDbExecute = "wdb.exec";

    // Resource limits (ADR 0125): a descriptor held by a write stream, and prlimit(2).
    public const string FsDescriptorOpen = "fs.fdopen";
    public const string FsDescriptorClose = "fs.fdclose";
    public const string ProcessSetLimit = "proc.rlimit";
}
