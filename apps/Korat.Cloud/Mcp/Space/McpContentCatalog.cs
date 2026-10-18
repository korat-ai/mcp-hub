using System.Text.Json.Nodes;

namespace Korat.Cloud.Mcp.Space;

/// <summary>A backend route for a namespaced prompt name or resource URI.</summary>
public sealed record McpContentRoute(string ServerId, string OriginalValue);

/// <summary>
/// Per-consumer catalog for MCP resources, resource templates, and prompts. Only servers admitted
/// for this consumer are registered, so resource routing can safely accept an unlisted absolute
/// URI while still remaining bounded to an already-admitted backend server.
/// </summary>
public sealed class McpContentCatalog
{
    private const string ResourceNamespace = "korat://servers/";

    private readonly object _lock = new();
    private Dictionary<string, ServerContent> _servers = new(StringComparer.Ordinal);
    private Dictionary<string, McpContentRoute> _promptRoutes = new(StringComparer.Ordinal);
    private Dictionary<string, McpContentRoute> _templateRoutes = new(StringComparer.Ordinal);
    private Dictionary<string, string> _serverIdsByEscapedId = new(StringComparer.Ordinal);
    private string _resourcesListJson = "";
    private string _resourceTemplatesListJson = "";
    private string _promptsListJson = "";

    public McpContentCatalog()
    {
        ApplySnapshot(BuildSnapshot(_servers));
    }

    public void SetServer(
        string serverId,
        string slug,
        JsonArray resources,
        JsonArray templates,
        JsonArray prompts)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        ArgumentException.ThrowIfNullOrEmpty(slug);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(prompts);

        var content = new ServerContent(
            slug,
            (JsonArray)resources.DeepClone(),
            (JsonArray)templates.DeepClone(),
            (JsonArray)prompts.DeepClone());

        lock (_lock)
        {
            var candidate = new Dictionary<string, ServerContent>(_servers, StringComparer.Ordinal)
            {
                [serverId] = content,
            };
            var snapshot = BuildSnapshot(candidate);
            _servers = candidate;
            ApplySnapshot(snapshot);
        }
    }

    public void RemoveServer(string serverId)
    {
        lock (_lock)
        {
            if (!_servers.ContainsKey(serverId)) return;
            var candidate = new Dictionary<string, ServerContent>(_servers, StringComparer.Ordinal);
            candidate.Remove(serverId);
            var snapshot = BuildSnapshot(candidate);
            _servers = candidate;
            ApplySnapshot(snapshot);
        }
    }

    public void RemoveExcept(IReadOnlySet<string> serverIds)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        lock (_lock)
        {
            var candidate = _servers
                .Where(pair => serverIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (candidate.Count == _servers.Count) return;
            var snapshot = BuildSnapshot(candidate);
            _servers = candidate;
            ApplySnapshot(snapshot);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_servers.Count == 0) return;
            var candidate = new Dictionary<string, ServerContent>(StringComparer.Ordinal);
            var snapshot = BuildSnapshot(candidate);
            _servers = candidate;
            ApplySnapshot(snapshot);
        }
    }

    public string ResourcesListJson()
    {
        lock (_lock) return _resourcesListJson;
    }

    public string ResourceTemplatesListJson()
    {
        lock (_lock) return _resourceTemplatesListJson;
    }

    public string PromptsListJson()
    {
        lock (_lock) return _promptsListJson;
    }

    public bool TryResolvePrompt(string name, out McpContentRoute? route)
    {
        lock (_lock)
        {
            if (_promptRoutes.TryGetValue(name, out var resolved))
            {
                route = resolved;
                return true;
            }

            route = null;
            return false;
        }
    }

    public bool TryResolveResource(string uri, out McpContentRoute? route)
    {
        lock (_lock)
        {
            route = null;
            if (!uri.StartsWith(ResourceNamespace, StringComparison.Ordinal)) return false;

            var idStart = ResourceNamespace.Length;
            var separator = uri.IndexOf('/', idStart);
            if (separator <= idStart) return false;

            var escapedServerId = uri[idStart..separator];
            if (!_serverIdsByEscapedId.TryGetValue(escapedServerId, out var serverId)) return false;

            var originalUri = uri[(separator + 1)..];
            if (!HasAbsoluteUriScheme(originalUri)) return false;

            route = new McpContentRoute(serverId, originalUri);
            return true;
        }
    }

    public bool TryResolveResourceTemplate(string uriTemplate, out McpContentRoute? route)
    {
        lock (_lock) return _templateRoutes.TryGetValue(uriTemplate, out route);
    }

    /// <summary>
    /// Adds a reversible server namespace without encoding the original URI. Keeping the original
    /// suffix byte-for-byte preserves RFC 6570 expressions as well as its query and fragment.
    /// </summary>
    public static string WrapResourceUri(string serverId, string originalUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        ArgumentNullException.ThrowIfNull(originalUri);
        return $"{ResourceNamespace}{Uri.EscapeDataString(serverId)}/{originalUri}";
    }

    /// <summary>
    /// Clones a backend result and rewrites only URI positions defined by MCP resource content.
    /// Structured content, text, metadata, and unrelated properties remain opaque.
    /// </summary>
    public static JsonObject RewriteResult(string serverId, JsonObject result)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        ArgumentNullException.ThrowIfNull(result);

        var rewritten = (JsonObject)result.DeepClone();

        if (rewritten["contents"] is JsonArray contents)
        {
            foreach (var content in contents.OfType<JsonObject>())
                RewriteUriProperty(serverId, content);
        }

        RewriteContent(serverId, rewritten["content"]);

        if (rewritten["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
                RewriteContent(serverId, message["content"]);
        }

        return rewritten;
    }

    private static CatalogSnapshot BuildSnapshot(IReadOnlyDictionary<string, ServerContent> servers)
    {
        var listedResources = new List<ListedDescriptor>();
        var listedTemplates = new List<ListedDescriptor>();
        var listedPrompts = new List<ListedDescriptor>();
        var promptRoutes = new Dictionary<string, McpContentRoute>(StringComparer.Ordinal);
        var templateRoutes = new Dictionary<string, McpContentRoute>(StringComparer.Ordinal);
        var serverIdsByEscapedId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (serverId, content) in servers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var escapedServerId = Uri.EscapeDataString(serverId);
            if (!serverIdsByEscapedId.TryAdd(escapedServerId, serverId))
                throw new InvalidOperationException($"Duplicate encoded MCP server id '{escapedServerId}'.");

            AddResourceDescriptors(serverId, content.Resources, listedResources);
            AddTemplateDescriptors(serverId, content.Templates, listedTemplates);
            foreach (var template in content.Templates.OfType<JsonObject>())
                if (TryGetNonEmptyString(template, "uriTemplate", out var original))
                    templateRoutes[WrapResourceUri(serverId, original)] = new(serverId, original);
            AddPromptDescriptors(serverId, content.Slug, content.Prompts, listedPrompts, promptRoutes);
        }

        var resources = new JsonArray { KoratGuide.ResourceDescriptor() };
        AddSorted(resources, listedResources);

        var templates = new JsonArray();
        AddSorted(templates, listedTemplates);

        var prompts = new JsonArray { KoratGuide.PromptDescriptor() };
        AddSorted(prompts, listedPrompts);

        return new CatalogSnapshot(
            new JsonObject { ["resources"] = resources }.ToJsonString(),
            new JsonObject { ["resourceTemplates"] = templates }.ToJsonString(),
            new JsonObject { ["prompts"] = prompts }.ToJsonString(),
            promptRoutes,
            templateRoutes,
            serverIdsByEscapedId);
    }

    private static void AddResourceDescriptors(
        string serverId,
        JsonArray descriptors,
        ICollection<ListedDescriptor> destination)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (descriptors[index] is not JsonObject descriptor ||
                !TryGetNonEmptyString(descriptor, "uri", out var originalUri) ||
                !HasAbsoluteUriScheme(originalUri))
                continue;

            var clone = (JsonObject)descriptor.DeepClone();
            var namespacedUri = WrapResourceUri(serverId, originalUri);
            clone["uri"] = namespacedUri;
            destination.Add(new ListedDescriptor(namespacedUri, serverId, index, clone));
        }
    }

    private static void AddTemplateDescriptors(
        string serverId,
        JsonArray descriptors,
        ICollection<ListedDescriptor> destination)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (descriptors[index] is not JsonObject descriptor ||
                !TryGetNonEmptyString(descriptor, "uriTemplate", out var originalTemplate))
                continue;

            var clone = (JsonObject)descriptor.DeepClone();
            var namespacedTemplate = WrapResourceUri(serverId, originalTemplate);
            clone["uriTemplate"] = namespacedTemplate;
            destination.Add(new ListedDescriptor(namespacedTemplate, serverId, index, clone));
        }
    }

    private static void AddPromptDescriptors(
        string serverId,
        string slug,
        JsonArray descriptors,
        ICollection<ListedDescriptor> destination,
        IDictionary<string, McpContentRoute> routes)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (descriptors[index] is not JsonObject descriptor ||
                !TryGetNonEmptyString(descriptor, "name", out var originalName))
                continue;

            var namespacedName = $"{slug}__{originalName}";
            var route = new McpContentRoute(serverId, originalName);
            if (!routes.TryAdd(namespacedName, route))
            {
                var existing = routes[namespacedName];
                throw new InvalidOperationException(
                    $"Duplicate namespaced MCP prompt '{namespacedName}' from servers " +
                    $"'{existing.ServerId}' and '{serverId}'. Server slugs must be unique.");
            }

            var clone = (JsonObject)descriptor.DeepClone();
            clone["name"] = namespacedName;
            destination.Add(new ListedDescriptor(namespacedName, serverId, index, clone));
        }
    }

    private static void AddSorted(JsonArray array, IEnumerable<ListedDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors
                     .OrderBy(item => item.SortKey, StringComparer.Ordinal)
                     .ThenBy(item => item.ServerId, StringComparer.Ordinal)
                     .ThenBy(item => item.SourceIndex))
            array.Add(descriptor.Descriptor);
    }

    private static void RewriteContent(string serverId, JsonNode? content)
    {
        if (content is JsonArray array)
        {
            foreach (var item in array)
                RewriteContentBlock(serverId, item as JsonObject);
            return;
        }

        RewriteContentBlock(serverId, content as JsonObject);
    }

    private static void RewriteContentBlock(string serverId, JsonObject? block)
    {
        if (block is null || !TryGetNonEmptyString(block, "type", out var type)) return;

        if (type == "resource" && block["resource"] is JsonObject embeddedResource)
            RewriteUriProperty(serverId, embeddedResource);
        else if (type == "resource_link" &&
                 TryGetNonEmptyString(block, "uri", out var uri) &&
                 !IsDirectHttpUri(uri))
            RewriteUriProperty(serverId, block);
    }

    private static bool IsDirectHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static void RewriteUriProperty(string serverId, JsonObject node)
    {
        if (TryGetNonEmptyString(node, "uri", out var uri))
            node["uri"] = WrapResourceUri(serverId, uri);
    }

    private static bool TryGetNonEmptyString(JsonObject node, string property, out string value)
    {
        if (node[property] is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var stringValue) &&
            !string.IsNullOrEmpty(stringValue))
        {
            value = stringValue;
            return true;
        }

        value = "";
        return false;
    }

    // RFC 3986 absolute-URI check without parsing or normalizing the opaque original URI.
    private static bool HasAbsoluteUriScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || !IsAsciiLetter(value[0])) return false;
        for (var i = 1; i < colon; i++)
        {
            var c = value[i];
            if (!IsAsciiLetter(c) && !char.IsAsciiDigit(c) && c is not '+' and not '-' and not '.')
                return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private void ApplySnapshot(CatalogSnapshot snapshot)
    {
        _resourcesListJson = snapshot.ResourcesListJson;
        _resourceTemplatesListJson = snapshot.ResourceTemplatesListJson;
        _promptsListJson = snapshot.PromptsListJson;
        _promptRoutes = snapshot.PromptRoutes;
        _templateRoutes = snapshot.TemplateRoutes;
        _serverIdsByEscapedId = snapshot.ServerIdsByEscapedId;
    }

    private sealed record ServerContent(
        string Slug,
        JsonArray Resources,
        JsonArray Templates,
        JsonArray Prompts);

    private sealed record ListedDescriptor(
        string SortKey,
        string ServerId,
        int SourceIndex,
        JsonObject Descriptor);

    private sealed record CatalogSnapshot(
        string ResourcesListJson,
        string ResourceTemplatesListJson,
        string PromptsListJson,
        Dictionary<string, McpContentRoute> PromptRoutes,
        Dictionary<string, McpContentRoute> TemplateRoutes,
        Dictionary<string, string> ServerIdsByEscapedId);
}
