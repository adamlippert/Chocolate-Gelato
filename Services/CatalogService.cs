using Gelato.Config;

namespace Gelato.Services;

public class CatalogService(GelatoStremioProviderFactory stremioFactory)
{
    public async Task<List<CatalogConfig>> GetCatalogsAsync(Guid userId)
    {
        var config = GelatoPlugin.Instance!.Configuration;
        var provider = stremioFactory.Create(userId);
        var manifest = await provider.GetManifestAsync();

        if (manifest?.Catalogs == null)
        {
            // Manifest unreachable — return what is persisted rather than an empty list.
            return config.Catalogs;
        }

        // Start from persisted rows so settings for catalogs missing from the current
        // manifest survive. A catalog vanishing from the manifest is usually transient
        // (addon disabled, upstream hiccup) and must not destroy the user's config.
        var merged = config.Catalogs.ToList();

        foreach (var mCatalog in manifest.Catalogs)
        {
            if (!mCatalog.IsImportable())
                continue;

            var existing = merged.FirstOrDefault(c =>
                c.Id == mCatalog.Id && c.Type == mCatalog.Type
            );

            if (existing is null)
            {
                merged.Add(
                    new CatalogConfig
                    {
                        Id = mCatalog.Id,
                        Type = mCatalog.Type,
                        Name = mCatalog.Name,
                        Enabled = false,
                        MaxItems = 0,
                        CreateCollection = false,
                        Url = "",
                    }
                );
            }
            else
            {
                existing.Name = mCatalog.Name;
            }
        }

        return merged;
    }

    public void UpdateCatalogConfig(CatalogConfig updatedConfig)
    {
        var config = GelatoPlugin.Instance!.Configuration;
        var existing = config.Catalogs.FirstOrDefault(c =>
            c.Id == updatedConfig.Id && c.Type == updatedConfig.Type
        );

        if (existing != null)
        {
            existing.Enabled = updatedConfig.Enabled;
            existing.MaxItems = updatedConfig.MaxItems;
            existing.CreateCollection = updatedConfig.CreateCollection;
        }
        else
        {
            config.Catalogs.Add(updatedConfig);
        }

        GelatoPlugin.Instance.SaveConfiguration();
    }

    public CatalogConfig? GetCatalogConfig(string id, string type)
    {
        return GelatoPlugin.Instance!.Configuration.Catalogs.FirstOrDefault(c =>
            c.Id == id && c.Type == type
        );
    }
}
