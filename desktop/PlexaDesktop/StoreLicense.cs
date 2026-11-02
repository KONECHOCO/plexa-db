using System.Text.Json;
using Windows.Services.Store;
using WinRT.Interop;

namespace PlexaDesktop;

public sealed record Entitlement(string Plan, string? Expires)
{
    /// Only Business opens the gateway to the office network: trial and Pro stay on this PC.
    public bool ListensOnNetwork => Plan == "business";
}

/// Reads and buys the Plexa DB plans through the Microsoft Store.
/// Partner Center setup: the app itself is the Pro plan (paid, with a 7-day free trial) and
/// Business is an add-on subscription whose product ID is BusinessToken.
public sealed class StoreLicense
{
    private const string BusinessToken = "plexa_business";
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);

    private readonly StoreContext _context = StoreContext.GetDefault();
    private readonly string _cacheFile;

    public StoreLicense(IntPtr window, string dataDir)
    {
        InitializeWithWindow.Initialize(_context, window);
        _cacheFile = Path.Combine(dataDir, "store-license.json");
    }

    public async Task<Entitlement> ReadAsync()
    {
#if DEBUG
        // Store APIs need a package published on the Store; this lets a debug build try each plan.
        var devPlan = Environment.GetEnvironmentVariable("PLEXA_DEV_PLAN");
        if (!string.IsNullOrEmpty(devPlan))
            return new Entitlement(devPlan, devPlan == "trial" ? DateTimeOffset.Now.AddDays(7).ToString("o") : null);
#endif
        try
        {
            // Never leave the window waiting on the Store: past the timeout the catch below applies.
            var license = await _context.GetAppLicenseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            var entitlement = FromLicense(license);
            RememberPaid(entitlement);
            return entitlement;
        }
        catch (Exception)
        {
            // The Store can be unreachable: a paying customer must not lose access because of that.
            return LastPaid() ?? new Entitlement("expired", null);
        }
    }

    private static Entitlement FromLicense(StoreAppLicense license)
    {
        foreach (var addOn in license.AddOnLicenses.Values)
            if (addOn.IsActive && addOn.InAppOfferToken == BusinessToken)
                return new Entitlement("business", addOn.ExpirationDate.ToString("o"));

        if (!license.IsActive) return new Entitlement("expired", null);
        return license.IsTrial
            ? new Entitlement("trial", license.ExpirationDate.ToString("o"))
            : new Entitlement("pro", null);
    }

    /// Returns "succeeded", "cancelled" or "error".
    public async Task<string> PurchaseAsync(string sku)
    {
        try
        {
            string? storeId = sku == "business" ? await BusinessStoreIdAsync() : await AppStoreIdAsync();
            if (storeId == null) return "error";
            var result = await _context.RequestPurchaseAsync(storeId);
            return result.Status switch
            {
                StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased => "succeeded",
                StorePurchaseStatus.NotPurchased => "cancelled",
                _ => "error",
            };
        }
        catch (Exception)
        {
            return "error";
        }
    }

    private async Task<string?> AppStoreIdAsync() =>
        (await _context.GetStoreProductForCurrentAppAsync()).Product?.StoreId;

    private async Task<string?> BusinessStoreIdAsync()
    {
        var result = await _context.GetAssociatedStoreProductsAsync(new[] { "Durable" });
        return result.Products.Values.FirstOrDefault(p => p.InAppOfferToken == BusinessToken)?.StoreId;
    }

    private void RememberPaid(Entitlement e)
    {
        try
        {
            if (e.Plan is "pro" or "business")
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(new { e.Plan, e.Expires, CheckedAt = DateTimeOffset.UtcNow }));
            else if (File.Exists(_cacheFile))
                File.Delete(_cacheFile);
        }
        catch (IOException) { }
    }

    private Entitlement? LastPaid()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_cacheFile));
            var root = doc.RootElement;
            if (DateTimeOffset.UtcNow - root.GetProperty("CheckedAt").GetDateTimeOffset() > OfflineGrace) return null;
            return new Entitlement(root.GetProperty("Plan").GetString()!, root.GetProperty("Expires").GetString());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
