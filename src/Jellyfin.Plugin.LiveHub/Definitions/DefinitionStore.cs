using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>
/// Persists Provider Definitions as JSON files (one per provider). Secrets are encrypted on save, masked when shown,
/// blanked on export and decrypted only in memory for runs.
/// </summary>
public sealed class DefinitionStore
{
    private readonly LiveHubPaths _paths;
    private readonly SecretProtector _protector;
    private readonly ILogger<DefinitionStore> _logger;
    private readonly ConcurrentDictionary<string, ProviderDefinition> _cache = new(StringComparer.Ordinal);
    private readonly object _ioLock = new();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefinitionStore"/> class.
    /// </summary>
    /// <param name="paths">Paths.</param>
    /// <param name="protector">Secret protector.</param>
    /// <param name="logger">Logger.</param>
    public DefinitionStore(LiveHubPaths paths, SecretProtector protector, ILogger<DefinitionStore> logger)
    {
        _paths = paths;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Raised when a definition is added, changed or removed (argument: provider id).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Gets all definitions (secrets still encrypted).</summary>
    /// <returns>The definitions.</returns>
    public IReadOnlyList<ProviderDefinition> List()
    {
        EnsureLoaded();
        return _cache.Values.OrderByDescending(d => d.Priority).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Gets a definition (secrets still encrypted).</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition or null.</returns>
    public ProviderDefinition? Get(string id)
    {
        EnsureLoaded();
        return _cache.TryGetValue(id, out var d) ? d : null;
    }

    /// <summary>Gets a copy with decrypted secrets, for a run (never persisted, never returned to clients).</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition or null.</returns>
    public ProviderDefinition? GetForRun(string id)
    {
        var d = Get(id);
        return d is null ? null : WithPlainSecrets(d);
    }

    /// <summary>Decrypts secrets on a copy.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>A copy with plain secrets.</returns>
    public ProviderDefinition WithPlainSecrets(ProviderDefinition definition)
    {
        var copy = DefinitionSerializer.Clone(definition);
        if (copy.Secrets is not null)
        {
            foreach (var key in copy.Secrets.Keys.ToList())
            {
                copy.Secrets[key] = _protector.Unprotect(copy.Secrets[key]);
            }
        }

        return copy;
    }

    /// <summary>Returns a copy safe for the UI: secrets masked.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The masked copy.</returns>
    public static ProviderDefinition Masked(ProviderDefinition definition)
    {
        var copy = DefinitionSerializer.Clone(definition);
        if (copy.Secrets is not null)
        {
            foreach (var key in copy.Secrets.Keys.ToList())
            {
                copy.Secrets[key] = string.IsNullOrEmpty(copy.Secrets[key]) ? string.Empty : SecretProtector.Mask;
            }
        }

        return copy;
    }

    /// <summary>Returns a copy safe for export: secrets blanked.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The export JSON.</returns>
    public static string Export(ProviderDefinition definition)
    {
        var copy = DefinitionSerializer.Clone(definition);
        if (copy.Secrets is not null)
        {
            foreach (var key in copy.Secrets.Keys.ToList())
            {
                copy.Secrets[key] = string.Empty;
            }
        }

        copy.Schema = "https://github.com/livehub/jellyfin-plugin-livehub/schemas/provider.schema.json";
        return DefinitionSerializer.Serialize(copy);
    }

    /// <summary>
    /// Merges masked secrets with the stored ones (the UI sends the mask back for unchanged secrets).
    /// </summary>
    /// <param name="incoming">The incoming definition.</param>
    /// <returns>The definition with real (plain or encrypted) secrets.</returns>
    public ProviderDefinition RestoreMaskedSecrets(ProviderDefinition incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var existing = Get(incoming.Id);
        if (incoming.Secrets is null || existing?.Secrets is null)
        {
            return incoming;
        }

        foreach (var key in incoming.Secrets.Keys.ToList())
        {
            if (incoming.Secrets[key] == SecretProtector.Mask && existing.Secrets.TryGetValue(key, out var stored))
            {
                incoming.Secrets[key] = stored;
            }
        }

        return incoming;
    }

    /// <summary>Validates and saves a definition (secrets get encrypted).</summary>
    /// <param name="definition">The definition (plain or masked secrets).</param>
    /// <returns>The validation result (nothing is saved when invalid).</returns>
    public ValidationResult Save(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureLoaded();
        var restored = RestoreMaskedSecrets(DefinitionSerializer.Clone(definition));
        var result = DefinitionValidator.Validate(restored);
        if (!result.IsValid)
        {
            return result;
        }

        if (restored.Secrets is not null)
        {
            foreach (var key in restored.Secrets.Keys.ToList())
            {
                restored.Secrets[key] = _protector.Protect(restored.Secrets[key]);
            }
        }

        restored.Schema = null;
        var json = DefinitionSerializer.Serialize(restored);
        lock (_ioLock)
        {
            var path = PathFor(restored.Id);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }

        _cache[restored.Id] = restored;
        _logger.LogInformation("Provider definition {Id} saved", restored.Id);
        Changed?.Invoke(this, restored.Id);
        return result;
    }

    /// <summary>Deletes a definition.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Whether it existed.</returns>
    public bool Delete(string id)
    {
        EnsureLoaded();
        if (!_cache.TryRemove(id, out _))
        {
            return false;
        }

        lock (_ioLock)
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Changed?.Invoke(this, id);
        return true;
    }

    /// <summary>Builds a unique id from a base.</summary>
    /// <param name="baseId">Desired id.</param>
    /// <returns>A free id.</returns>
    public string UniqueId(string baseId)
    {
        EnsureLoaded();
        var slug = new string((baseId ?? "provider").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length < 2)
        {
            slug = "provider";
        }

        if (slug.Length > 50)
        {
            slug = slug[..50].Trim('-');
        }

        var id = slug;
        var n = 2;
        while (_cache.ContainsKey(id))
        {
            id = slug + "-" + n++;
        }

        return id;
    }

    private string PathFor(string id)
    {
        // Ids are validated by the validator ([a-z0-9_-]); this guard prevents path traversal anyway.
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid provider id.", nameof(id));
        }

        return Path.Combine(_paths.ProvidersDir, id + ".json");
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_ioLock)
        {
            if (_loaded)
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_paths.ProvidersDir, "*.json"))
            {
                try
                {
                    var def = DefinitionSerializer.TryParse(File.ReadAllText(file), out var error);
                    if (def is null)
                    {
                        _logger.LogError("Invalid provider definition {File}: {Error}", Path.GetFileName(file), error);
                        continue;
                    }

                    _cache[def.Id] = def;
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Unable to read provider definition {File}", Path.GetFileName(file));
                }
            }

            _loaded = true;
        }
    }
}
