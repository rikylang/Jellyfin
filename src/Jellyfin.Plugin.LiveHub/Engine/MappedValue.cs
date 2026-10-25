using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// The result of evaluating a field: a scalar text, a list of texts, and the raw first JSON node.
/// </summary>
public readonly record struct MappedValue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MappedValue"/> struct.
    /// </summary>
    /// <param name="text">Scalar text.</param>
    /// <param name="list">List value.</param>
    /// <param name="raw">Raw node.</param>
    public MappedValue(string? text, IReadOnlyList<string?>? list = null, JsonNode? raw = null)
    {
        Text = text;
        List = list;
        Raw = raw;
    }

    /// <summary>Gets an empty value.</summary>
    public static MappedValue Empty => default;

    /// <summary>Gets the scalar text (first element for lists).</summary>
    public string? Text { get; }

    /// <summary>Gets the list value (null for scalars).</summary>
    public IReadOnlyList<string?>? List { get; }

    /// <summary>Gets the raw first JSON node, when the value came from a path.</summary>
    public JsonNode? Raw { get; }

    /// <summary>Gets a value indicating whether the value is a list.</summary>
    public bool IsList => List is not null;

    /// <summary>Gets a value indicating whether there is no usable value.</summary>
    public bool IsEmpty => IsList ? List!.All(string.IsNullOrWhiteSpace) : string.IsNullOrWhiteSpace(Text);

    /// <summary>Creates a scalar.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static MappedValue Of(string? text) => new(text);

    /// <summary>Creates a list.</summary>
    /// <param name="items">The items.</param>
    /// <returns>The value.</returns>
    public static MappedValue OfList(IReadOnlyList<string?> items) => new(items.Count > 0 ? items[0] : null, items);

    /// <summary>Gets all non empty texts.</summary>
    /// <returns>The texts.</returns>
    public IEnumerable<string> All()
    {
        if (IsList)
        {
            foreach (var s in List!)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    yield return s;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(Text))
        {
            yield return Text!;
        }
    }
}
