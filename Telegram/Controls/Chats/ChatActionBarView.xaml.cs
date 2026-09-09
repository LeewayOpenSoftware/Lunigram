//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Colors = Microsoft.UI.Colors;

namespace Telegram.Controls.Chats
{
    public sealed partial class ChatActionBarView : UserControl
    {
        public DialogViewModel ViewModel => DataContext as DialogViewModel;

        private ChatView _chatView;

        public ChatActionBarView()
        {
            InitializeComponent();

            _collapsed = new SlidePanel.SlideState(this, false, 32);
        }

        public float AnimatedHeight => _collapsed ? 0 : 32;

        public void InitializeParent(ChatView chatView)
        {
            _chatView = chatView;
        }

        public void UpdateChatActionBar(Chat chat)
        {
            if (chat == null)
            {
                ShowHide(false);
                return;
            }

            if (chat.ActionBar != null)
            {
                LayoutRoot.ColumnDefinitions.Clear();
                LayoutRoot.Children.Clear();
            }

#if LINUX
            // Una banda cuyo unico boton no esta en el subconjunto no se ensena vacia: se apaga.
            // Ver la rama de ChatActionBarInviteMembers.
            var unsupported = false;
#endif

            //ChatActionBarAddContact;
            //ChatActionBarInviteMembers;
            //ChatActionBarJoinRequest;
            //ChatActionBarReportAddBlock;
            //ChatActionBarReportSpam;
            //ChatActionBarReportUnrelatedLocation;
            //ChatActionBarSharePhoneNumber;

            if (chat.ActionBar is ChatActionBarAddContact)
            {
                var user = ViewModel.ClientService.GetUser(chat);
                if (user != null)
                {
                    CreateButton(string.Format(Strings.AddContactFullChat, user.FirstName.ToUpper()), ViewModel.AddToContacts);
                }
                else
                {
                    CreateButton(Strings.AddContactChat, ViewModel.AddToContacts);
                }
            }
            else if (chat.ActionBar is ChatActionBarInviteMembers)
            {
#if LINUX
                // PARIDAD M13, la otra mitad. chatActionBarInviteMembers es la banda que TDLib
                // pone en un grupo en el que no hay nadie mas que su creador -- o sea, EXACTAMENTE
                // el grupo que crea este port, porque NewGroupPopup no tiene selector de miembros
                // (ChooseChatsPopup son 3.443 lineas y esta fuera del subconjunto). Y el boton que
                // esta banda dibuja lleva a ese mismo selector que falta: DialogViewModel.Invite
                // tiene el cuerpo entero bajo #if !LINUX.
                //
                // O sea que era el peor caso posible: una banda que aparece SOLA justo despues de
                // crear el grupo, que anuncia con todas las letras lo unico que el port no sabe
                // hacer, y que al pulsarla no hace nada. Aqui no se dibuja el boton y, con
                // `unsupported` mas abajo, tampoco la banda: el chat se abre sin ella. La banda
                // vuelve entera con el selector, y el hueco queda declarado en PARIDAD M13 en vez
                // de fingido.
                unsupported = true;
#else
                CreateButton(Strings.GroupAddMembers.ToUpper(), ViewModel.Invite);
#endif
            }
            else if (chat.ActionBar is ChatActionBarJoinRequest joinRequest)
            {

            }
            else if (chat.ActionBar is ChatActionBarReportAddBlock reportAddBlock)
            {
                if (reportAddBlock.CanUnarchive)
                {
                    CreateButton(Strings.Unarchive.ToUpper(), ViewModel.Unarchive);
                    CreateButton(Strings.ReportSpamUser, ViewModel.ReportSpam, column: 1, danger: true);
                }
                else
                {
                    CreateButton(Strings.ReportSpamUser, ViewModel.ReportSpam, danger: true);
                    CreateButton(Strings.AddContactChat, ViewModel.AddToContacts, column: 1);
                }
            }
            else if (chat.ActionBar is ChatActionBarReportSpam reportSpam)
            {
                var user = ViewModel.ClientService.GetUser(chat);
                if (user != null)
                {
                    CreateButton(Strings.ReportSpamUser, ViewModel.ReportSpam, danger: true);
                }
                else
                {
                    CreateButton(Strings.ReportSpamAndLeave, ViewModel.ReportSpam, danger: true);
                }
            }
            else if (chat.ActionBar is ChatActionBarSharePhoneNumber)
            {
                CreateButton(Strings.ShareMyPhone, ViewModel.ShareMyContact);
            }

#if LINUX
            ShowHide(chat.ActionBar != null && !unsupported);
#else
            ShowHide(chat.ActionBar != null);
#endif
        }

        private Button CreateButton(string text, Action command, int column = 0, bool danger = false)
        {
            var button = CreateButton(text, column, danger);

            void handler(object sender, RoutedEventArgs e)
            {
                //button.Click -= handler;
                command();
            }

            button.Click += handler;
            return button;
        }

        private Button CreateButton(string text, int column = 0, bool danger = false)
        {
            var label = new TextBlock();
            label.Style = BootStrapper.Current.Resources["CaptionTextBlockStyle"] as Style;
            label.FontWeight = FontWeights.SemiBold;
            label.Text = text;

            var button = new Button();
            button.Style = BootStrapper.Current.Resources[danger ? "DangerTextButtonStyle" : "AccentTextButtonStyle"] as Style;
            button.Background = new SolidColorBrush(Colors.Transparent);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Content = label;

            LayoutRoot.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(button, column);

            LayoutRoot.Children.Add(button);
            return button;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.RemoveActionBar();
        }

        private SlidePanel.SlideState _collapsed;

        private void ShowHide(bool show)
        {
            if (_collapsed != show)
            {
                return;
            }

            _collapsed.IsVisible = show;
            _chatView.UpdateMessagesHeaderPadding();
        }

        public IEnumerable<UIElement> GetAnimatableVisuals()
        {
            if (_collapsed)
            {
                yield break;
            }

            yield return RemoveButton;
        }
    }
}
