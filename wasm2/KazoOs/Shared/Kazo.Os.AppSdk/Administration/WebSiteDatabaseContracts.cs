namespace Kazo.Os.AppSdk.Administration;

/// <summary>One browser database of a web site (ADR 0088, section 4), as the Diagnostic app lists it.</summary>
/// <param name="Name">The database name, <c>hackweb.&lt;app id&gt;</c>.</param>
/// <param name="AppId">The site's app id.</param>
/// <param name="Version">The stored version.</param>
/// <param name="Stores">The object stores and their record counts, by name.</param>
/// <param name="SizeBytes">UTF-8 size of the records' JSON (keys and values): an estimate, as a browser shows per origin.</param>
public sealed record HackWebSiteDatabaseInfo(
    string Name,
    string AppId,
    int Version,
    IReadOnlyList<HackWebSiteDatabaseStoreInfo> Stores,
    long SizeBytes)
{
    /// <summary>Gets the number of records of every store.</summary>
    public int RecordCount => Stores.Sum(store => store.RecordCount);
}

/// <summary>An object store of a site database.</summary>
/// <param name="Name">Store name.</param>
/// <param name="RecordCount">Number of records.</param>
public sealed record HackWebSiteDatabaseStoreInfo(string Name, int RecordCount);
