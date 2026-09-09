//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Telegram.Controls.Chats
{
    // The autocomplete SOURCES, lifted verbatim out of Controls/Chats/ChatTextBox.cs.
    //
    // They are copied rather than reached: upstream declares them NESTED inside
    // `ChatTextBox : FormattedTextBox`, and that file cannot enter the subset because
    // FormattedTextBox is a RichEditBox. Nesting them in the Linux `partial class ChatTextBox`
    // keeps the enclosing type -- so the call sites read exactly as upstream's, unqualified --
    // while the file itself stays out.
    //
    // Not one line of their bodies is edited. They talk to TDLib and know nothing about the
    // editor: SearchChatMembers/GetTopChats for @, SearchEmojis + GetStickers for :,
    // SearchHashtags for #. That is precisely why the plain TextBox can reuse them whole, and
    // why the popup shows REAL results rather than a shape.
    public partial class ChatTextBox
    {
        public partial class UsernameCollection : DiffObservableCollection<object>, IAutocompleteCollection, ISupportIncrementalLoading
        {
            private readonly IClientService _clientService;
            private readonly long _chatId;
            private readonly MessageTopic _topicId;
            private readonly string _query;

            private readonly bool _bots;
            private readonly bool _guestBots;
            private readonly bool _members;
            private readonly bool _self;

            private bool _hasMore = true;

            public UsernameCollection(IClientService clientService, long chatId, MessageTopic topicId, string query, bool bots, bool guestBots, bool members, bool self)
            {
                _clientService = clientService;
                _chatId = chatId;
                _topicId = topicId;
                _query = query;

                _bots = bots;
                _guestBots = guestBots;
                _members = members;
                _self = self;
            }

            public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
            {
                return AsyncInfo.Run(async token =>
                {
                    // There are two askers - the list once it is measured, and whoever primes the
                    // first page while it still is not - and _hasMore is cleared as this starts
                    // rather than when it ends, so it doubles as the in-flight flag. Without this
                    // the second one through would add every member a second time.
                    if (!_hasMore)
                    {
                        return new LoadMoreItemsResult();
                    }

                    count = 0;
                    _hasMore = false;

                    if (_bots)
                    {
                        var response = await _clientService.SendAsync(new GetTopChats(new TopChatCategoryInlineBots(), 10));
                        if (response is Telegram.Td.Api.Chats chats)
                        {
                            foreach (var id in chats.ChatIds)
                            {
                                var user = _clientService.GetUser(_clientService.GetChat(id));
                                if (user != null && (user.HasActiveUsername(_query, out _) || ClientEx.SearchByPrefix(user.FullName(), _query)))
                                {
                                    Add(user);
                                    count++;
                                }
                            }
                        }
                    }

                    if (_guestBots)
                    {
                        var response = await _clientService.SendAsync(new GetTopChats(new TopChatCategoryGuestBots(), 10));
                        if (response is Telegram.Td.Api.Chats chats)
                        {
                            foreach (var id in chats.ChatIds)
                            {
                                var user = _clientService.GetUser(_clientService.GetChat(id));
                                if (user != null && (user.HasActiveUsername(_query, out _) || ClientEx.SearchByPrefix(user.FullName(), _query)))
                                {
                                    Add(user);
                                    count++;
                                }
                            }
                        }
                    }

                    if (_members)
                    {
                        if (_self && string.IsNullOrEmpty(_query) && _clientService.TryGetUser(_clientService.Options.MyId, out Td.Api.User self))
                        {
                            Add(self);
                            count++;
                        }

                        var response = await _clientService.SendAsync(new SearchChatMembers(_chatId, _query, 20, new ChatMembersFilterMention(_topicId)));
                        if (response is ChatMembers members)
                        {
                            foreach (var member in members.Members)
                            {
                                if (_clientService.TryGetUser(member.MemberId, out Td.Api.User user))
                                {
                                    if (user.Id == _clientService.Options.MyId)
                                    {
                                        continue;
                                    }

                                    Add(user);
                                    count++;
                                }
                            }
                        }
                    }

                    return new LoadMoreItemsResult { Count = count };
                });
            }

            public bool HasMoreItems => _hasMore;

            public string Query => _query;

            public Orientation Orientation => Orientation.Vertical;

            public bool InsertOnKeyDown => true;
        }

        public partial class EmojiCollection : DiffObservableCollection<object>, IAutocompleteCollection, ISupportIncrementalLoading
        {
            private readonly IClientService _clientService;
            private readonly string _query;
            private readonly string _inputLanguage;
            private readonly long _chatId;

            private bool _hasMore = true;

            private string _emoji;

            public EmojiCollection(IClientService clientService, string query, long chatId)
            {
                _clientService = clientService;
                _query = query.Replace('_', ' ');
                _inputLanguage = NativeUtils.GetKeyboardCulture();
                _chatId = chatId;
            }

            public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
            {
                return AsyncInfo.Run(async token =>
                {
                    count = 0;

                    if (_emoji == null)
                    {
                        var response = await _clientService.SendAsync(new SearchEmojis(_query, new[] { _inputLanguage }));
                        if (response is EmojiKeywords emojis)
                        {
                            var results = new List<string>();
                            var recent = new Dictionary<int, string>();

                            var distinct = emojis.EmojiKeywordsValue
                                .DistinctBy(x => x.Emoji)
                                .Select(x => x.Emoji);

                            foreach (var emoji in distinct)
                            {
                                var index = _clientService.RecentEmoji.Items.IndexOf(emoji);
                                if (index >= 0)
                                {
                                    recent[index] = emoji;
                                }
                                else
                                {
                                    results.Add(emoji);
                                }
                            }

                            foreach (var emoji in recent.OrderByDescending(x => x.Key))
                            {
                                results.Insert(0, emoji.Value);
                            }

                            _emoji = string.Join(" ", results);

                            foreach (var emoji in results)
                            {
                                Add(new EmojiData(emoji));
                                count++;
                            }

                            return new LoadMoreItemsResult { Count = count };
                        }
                    }

                    if (_emoji?.Length > 0)
                    {
                        var response = await _clientService.SendAsync(new GetStickers(new StickerTypeCustomEmoji(), _emoji, 1000, _chatId));
                        if (response is Stickers stickers)
                        {
                            foreach (var sticker in stickers.StickersValue)
                            {
                                Add(sticker);
                                count++;
                            }
                        }
                    }

                    _hasMore = false;
                    return new LoadMoreItemsResult { Count = count };
                });
            }

            public bool HasMoreItems => _hasMore;

            public string Query => _query;

            public Orientation Orientation => Orientation.Horizontal;

            public bool InsertOnKeyDown => true;
        }

        public partial class SearchHashtagsCollection : DiffObservableCollection<object>, IAutocompleteCollection, ISupportIncrementalLoading
        {
            private readonly IClientService _clientService;
            private readonly string _query;

            private bool _hasMore = true;

            public SearchHashtagsCollection(IClientService clientService, string query)
            {
                _clientService = clientService;
                _query = query;
            }

            public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
            {
                return AsyncInfo.Run(async token =>
                {
                    count = 0;
                    _hasMore = false;

                    var response = await _clientService.SendAsync(new SearchHashtags(_query, 20));
                    if (response is Hashtags hashtags)
                    {
                        foreach (var value in hashtags.HashtagsValue)
                        {
                            Add("#" + value);
                            count++;
                        }
                    }

                    return new LoadMoreItemsResult
                    {
                        Count = count
                    };
                });
            }

            public bool HasMoreItems => _hasMore;

            public string Query => _query;

            public Orientation Orientation => Orientation.Vertical;

            public bool InsertOnKeyDown => true;
        }
    }
}
