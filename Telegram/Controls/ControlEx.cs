//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System.Runtime.CompilerServices;
using Telegram.Native.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace Telegram.Controls
{
    // TODO: not a big fan, for now it's only inherited by ChatCell, but it would be cool to use it everywhere some day, as it allows producing cleaner code
    // It replaces:
    // WhateverTemplatePart = GetTemplateChild(nameof(WhateverTemplatePart)) as WhateverType;
    // with:
    // LoadTemplateChild(ref WhateverTemplatePart);
    public partial class ControlEx2 : ControlEx
    {
        protected void LoadTemplateChild<T>(ref T element, [CallerArgumentExpression("element")] string name = null)
#if LINUX
            where T : class, DependencyObject // DependencyObject is an interface on Uno: `as T` and `= null` need the class constraint
#else
            where T : DependencyObject
#endif
        {
            element ??= GetTemplateChild(name) as T;
        }

        protected void UnloadTemplateChild<T>(ref T element)
#if LINUX
            where T : class, DependencyObject
#else
            where T : DependencyObject
#endif
        {
            if (element != null)
            {
                XamlMarkupHelper.UnloadObject(element);
                element = null;
            }
        }
    }
}
