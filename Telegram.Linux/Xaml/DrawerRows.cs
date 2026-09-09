//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Telegram.Common;

namespace Telegram.Controls.Drawers
{
    /// <summary>
    /// One ROW of a drawer: either a group's title, or a single line of up to <c>columns</c> items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE INVARIANT THIS TYPE EXISTS TO HOLD: nowhere in the drawer's visual tree is there an
    /// ItemsRepeater whose measured size on the stacking axis depends on realising its children.
    /// </para>
    /// <para>
    /// It took three measured failures to arrive at that sentence, and they are worth keeping
    /// because each one killed a plausible cure. A nested repeater with no Height is measured with
    /// (width, Infinity), realises against a viewport it has not got, and reports a height that
    /// changes the outer extent -- which moves the next group and changes ITS viewport. Giving it a
    /// LITERAL height killed its realisation outright. Giving it a height through a converter never
    /// evaluated at all. And giving it a correct precomputed height through a plain binding DID
    /// evaluate -- 200, 2440, 1120 for the first three groups -- and still collapsed, because the
    /// height bounds what the repeater REPORTS, not how much it realises: Measure(316, Infinity)
    /// still walked all twenty-seven cells. The realisation volume is the storm, so the nesting
    /// itself had to go.
    /// </para>
    /// <para>
    /// So the source is flattened to rows before it is ever handed over. A row's height is known
    /// from the data -- one cell, or one title -- and a row holds a FIXED, small number of cells,
    /// so the outer repeater virtualises rows whose size it never has to discover, and the only
    /// thing inside a row is a bounded list that builds its children once and measures them.
    /// </para>
    /// </remarks>
    public partial class DrawerRow
    {
        private DrawerRow(string title, IList<object> cells, double height)
        {
            Title = title;
            Cells = cells;
            Height = height;
        }

        /// <summary>A group title, or null on a row of items.</summary>
        public string Title { get; }

        /// <summary>The items on this row, or null on a title row. Never more than the column count.</summary>
        public IList<object> Cells { get; }

        /// <summary>Known before anything is measured, which is the whole point.</summary>
        public double Height { get; }

        /// <summary>
        /// Flattens groups into rows for a given column count and cell size.
        /// </summary>
        /// <remarks>
        /// A group with no title contributes no title row -- that is how the flat search results
        /// and a single-set popup show no header, exactly as the GroupStyle's own template did by
        /// binding a null through a NullToVisibilityConverter.
        /// </remarks>
        public static List<DrawerRow> Build(IEnumerable groups, int columns, double cell, double titleHeight)
        {
            var rows = new List<DrawerRow>();

            if (groups == null)
            {
                return rows;
            }

            if (columns < 1)
            {
                columns = 1;
            }

            foreach (var group in groups)
            {
                var title = TitleOf(group);

                if (!string.IsNullOrEmpty(title))
                {
                    rows.Add(new DrawerRow(title, null, titleHeight));
                }

                if (ItemsOf(group) is not IList items)
                {
                    continue;
                }

                for (int i = 0; i < items.Count; i += columns)
                {
                    var line = new List<object>(columns);

                    for (int j = i; j < i + columns && j < items.Count; j++)
                    {
                        line.Add(items[j]);
                    }

                    rows.Add(new DrawerRow(null, line, cell));
                }
            }

            return rows;
        }

        private static readonly Dictionary<Type, PropertyInfo> _items = new();
        private static readonly Dictionary<Type, PropertyInfo> _titles = new();

        /// <summary>
        /// Reads a member off a group whatever kind of group it is.
        /// </summary>
        /// <remarks>
        /// Reflection, deliberately, and it is the same reflection the XAML used to do binding
        /// {Binding Stickers} against these very objects. The four group kinds declare that member
        /// with four different types -- MvxObservableCollection&lt;object&gt;, EmojiData[],
        /// MvxObservableCollection&lt;StickerViewModel&gt;, and plain object on the drawers' own
        /// flat-search wrapper -- so nothing could be added to IDrawerGroup without three explicit
        /// implementations in files Windows shares. Cached per type; there are four of them.
        /// </remarks>
        private static PropertyInfo Property(Dictionary<Type, PropertyInfo> cache, Type type, string name)
        {
            lock (cache)
            {
                if (!cache.TryGetValue(type, out var property))
                {
                    property = type.GetProperty(name);
                    cache[type] = property;
                }

                return property;
            }
        }

        private static object ItemsOf(object group)
        {
            if (group == null)
            {
                return null;
            }

            var property = Property(_items, group.GetType(), "Stickers");

            if (property == null)
            {
                Logger.Warning($"drawer rows: {group.GetType().Name} has no Stickers; it will contribute no rows");
                return null;
            }

            return property.GetValue(group);
        }

        private static string TitleOf(object group)
        {
            if (group == null)
            {
                return null;
            }

            return Property(_titles, group.GetType(), "Title")?.GetValue(group) as string;
        }
    }
}
