//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Upstream 12.10 renamed the native class PlaceholderImageHelper to Direct2DDevice (and
// Common/PlaceholderHelper.cs to Common/Direct2D.cs with it). This head builds a managed
// stand-in that still carries the old name in Telegram.Linux/Native/PlaceholderImageHelper.cs,
// and it is named from nine files; one global alias is cheaper than renaming the type and
// keeps the stub's own history readable.
global using Direct2DDevice = Telegram.Native.PlaceholderImageHelper;

// Upstream 12.10 renamed its host page RootPage -> RootWindow. This head has its own RootPage
// (Telegram.Linux/Hubs/RootPage.xaml.cs) in the same namespace playing the same role, so shared
// code that names RootWindow resolves to it here.
global using RootWindow = Telegram.Views.Host.RootPage;
