//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
// Telegram.Linux -- stand-in for the sliver of Windows.Data.Json that Controls/DiceView.cs's
// MergeReels uses to splice the left/center/right Lottie reels of a slot-machine dice into one
// animation (PORTING.md section 6 lists Windows.Data.Json among the APIs Uno Skia does not
// implement at all). Backed by System.Text.Json.Nodes, which already has a very similar mutable
// JSON-tree API -- this is a thin adapter, not a reimplementation of a JSON parser.
//
// One real semantic gap versus the real WinRT type: System.Text.Json.Nodes enforces single
// ownership (a JsonNode already attached to one tree throws if added to another), which is
// exactly what MergeReels does -- it pulls asset/layer objects out of one parsed document and
// appends them into another. JsonArray.Add here deep-clones for that reason; see the comment
// there. Deliberately narrow: only Parse/GetNamedArray/GetNamedString/SetNamedValue/TryGetValue/
// Add/GetObject/GetString/ToString, the exact surface MergeReels calls. A second caller needing
// more of the real API needs a real port of this file, not an extension.
//
using System;
using System.Collections;
using System.Collections.Generic;
using STJ = System.Text.Json.Nodes;

namespace Windows.Data.Json
{
    public interface IJsonValue
    {
        JsonObject GetObject();

        string GetString();
    }

    public sealed class JsonValue : IJsonValue
    {
        private readonly STJ.JsonNode _node;

        internal JsonValue(STJ.JsonNode node)
        {
            _node = node;
        }

        internal STJ.JsonNode Node => _node;

        public static IJsonValue CreateStringValue(string value)
        {
            return new JsonValue(STJ.JsonValue.Create(value));
        }

        public JsonObject GetObject()
        {
            throw new InvalidOperationException("This JsonValue does not hold an object.");
        }

        public string GetString()
        {
            return _node?.GetValue<string>();
        }
    }

    public sealed class JsonObject : IJsonValue
    {
        internal STJ.JsonObject Node { get; }

        internal JsonObject(STJ.JsonObject node)
        {
            Node = node;
        }

        public static JsonObject Parse(string input)
        {
            return new JsonObject((STJ.JsonObject)STJ.JsonNode.Parse(input));
        }

        public JsonArray GetNamedArray(string name)
        {
            return new JsonArray((STJ.JsonArray)Node[name]);
        }

        public string GetNamedString(string name)
        {
            return Node[name]?.GetValue<string>();
        }

        public void SetNamedValue(string name, IJsonValue value)
        {
            Node[name] = Unwrap(value)?.DeepClone();
        }

        public bool TryGetValue(string key, out IJsonValue value)
        {
            if (Node.TryGetPropertyValue(key, out var node) && node != null)
            {
                value = Wrap(node);
                return true;
            }

            value = null;
            return false;
        }

        public JsonObject GetObject()
        {
            return this;
        }

        public string GetString()
        {
            throw new InvalidOperationException("This JsonValue does not hold a string.");
        }

        public override string ToString()
        {
            return Node.ToJsonString();
        }

        internal static STJ.JsonNode Unwrap(IJsonValue value)
        {
            return value switch
            {
                JsonValue v => v.Node,
                JsonObject o => o.Node,
                JsonArray a => a.Node,
                _ => null
            };
        }

        internal static IJsonValue Wrap(STJ.JsonNode node)
        {
            return node switch
            {
                STJ.JsonObject o => new JsonObject(o),
                STJ.JsonArray a => new JsonArray(a),
                _ => new JsonValue(node)
            };
        }
    }

    public sealed class JsonArray : IJsonValue, IEnumerable<IJsonValue>
    {
        internal STJ.JsonArray Node { get; }

        internal JsonArray(STJ.JsonArray node)
        {
            Node = node;
        }

        // Deep-clones: `value` almost always still belongs to a DIFFERENT parsed document here
        // (MergeReels moves asset/layer objects between the three reels' trees), and
        // System.Text.Json.Nodes throws if a node already parented elsewhere is added as-is.
        public void Add(IJsonValue value)
        {
            Node.Add(JsonObject.Unwrap(value)?.DeepClone());
        }

        public JsonObject GetObject()
        {
            throw new InvalidOperationException("This JsonValue does not hold an object.");
        }

        public string GetString()
        {
            throw new InvalidOperationException("This JsonValue does not hold a string.");
        }

        public IEnumerator<IJsonValue> GetEnumerator()
        {
            foreach (var node in Node)
            {
                yield return JsonObject.Wrap(node);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
