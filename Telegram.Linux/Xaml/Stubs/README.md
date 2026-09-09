# `Telegram.Linux/Xaml/Stubs/` — one stub, one file

Controls that `ChatView.xaml` instantiates but that are out of the compiled subset (composer on
`RichEditBox`, inline-bot results, bot keyboards, chat header bands with Win2D/InteractionTracker).
Each stub keeps exactly the surface `ChatView.xaml(.cs)` touches and renders nothing, so the XAML and
the code-behind compile unchanged.

Until 2026-09-02 all of them lived in a single `Xaml/ChatViewStubs.cs`, which had become the hottest
file in the tree: every parallel port batch that graduated a control had to edit it, and three
different authors deleting three different blocks from it in the same week is how a merge goes wrong.
Split so a batch that implements one control touches one file.

| File | Class | Namespace | ChatView surface it satisfies |
| --- | --- | --- | --- |
| `ChatHeaderStub.cs` | `ChatHeaderStub` | `Telegram.Controls.Chats` | abstract base of the seven bands: `AnimatedHeight`, `InitializeParent`, `GetAnimatableVisuals` |
| `ChatGroupCallHeader.cs` | `ChatGroupCallHeader` | `Telegram.Controls.Chats` | `JoinClick`, `ShowHide`, `UpdateGroupCall` |
| `ChatTextDocument.cs` | `ChatTextDocument` | `Telegram.Controls.Chats` | `Selection`, `GetRange` |
| `ChatTextRange.cs` | `ChatTextRange` | `Telegram.Controls.Chats` | `StartPosition`, `EndPosition` |
| `ChatTextBox.cs` | `ChatTextBox` | `Telegram.Controls.Chats` | the composer surface, minus sending (live, in `../ChatTextBoxSend.cs`) |
| `InlineBotResultsView.cs` | `InlineBotResultsView` | `Telegram.Controls` | `ItemClick`, `UpdateChatPermissions`, `UpdateCornerRadius` |
| `ReplyMarkupPanel.cs` | `ReplyMarkupPanel` | `Telegram.Controls` | `ButtonClick`, `Update` |
| `MoreButton.cs` | `MoreButton` | `Telegram.Controls` | a `SettingsButton` without the LottieGen icon |
| `InstantContent.cs` | `InstantContent` | `Telegram.Controls.Messages.Content` | `UpdateView`, `ShowHideSkeleton`, collapsed by default |

`ChatPinnedMessage` used to be the sixteenth entry. Since 2026-09-02 the real Linux band lives in
`../ChatPinnedMessageLinux.cs` and derives from `MessageReferenceBase`, not from `ChatHeaderStub`.

`ChatJoinRequestsHeader` left in u-022, as the real upstream control: `ShowJoinRequests()` was
un-fenced and `ChatJoinRequestsPopup` + `ChatJoinRequestsViewModel` entered the subset with it, so
the band's click now reaches `ProcessChatJoinRequest`. **Six of the seven header bands draw.**

Four more header bands left in u-013: `ChatTranslateBar`, `ChatConnectedBotHeader`,
`ChatAccountInfoHeader` and `ChatSponsoredHeader` are now the **real upstream controls**, compiled
from `Telegram/Controls/Chats/` through `fase1/extra-files.txt`. Nothing was written for them here:
the measurement was that four of the six resolved against the subset as they stand. Only one band
remains stubbed, and for a reason that is not about this directory:

- **`ChatGroupCallHeader`** — the last one. `JoinClick` reaches `VoipCoordinator.JoinGroupCall`,
  which on Linux is `Logger.Info("Group calls: the native side is ready, the UI is not ported yet")`
  and nothing else (`VoipCoordinator.cs:201`). Drawing it is an exact dead button.

  **Its blocker count is measured, not estimated** — u-022 put it in the subset, built, and took it
  back out:
  - *CS0400 from Uno's generator*: **closed**. The one-line fix (subscribe `RecentUserHeadChanged`
    in the code-behind instead of from the markup) is already applied to this band's `.xaml` and
    `.xaml.cs` on that branch, and was compiled in that pass.
  - *Dead action*: intact, and it is the one that decides.
  - *A third nobody had seen*: `Telegram/Services/Calls/VoipGroupCall.cs` is not in the subset —
    `ChatGroupCallHeader.xaml.cs:76` reads `VoipService.ActiveCall is VoipGroupCall` to know whether
    you are already in the call. After the CS0400 fix it was the **only** error left.

  So when the group-call UI lands (P-17), `VoipGroupCall` arrives with it and this band needs
  **nothing else**: one line in `extra-files.txt` and delete this file.

## Rules

- **Every class here is load-bearing.** Two of them (`ChatHeaderStub`, `ChatTextRange`) read as zero
  references from a `.cs` sweep and are still load-bearing — one is an abstract base, the other a
  return type. Several others are referenced only from `Views/ChatView.xaml`, which a `.cs` grep never
  sees. Do not delete a file here because a search came back empty.
- **Implementing one means deleting its file**, not editing it in place: the real control has a
  different base class more often than not, and a leftover partial declaration is a `CS0263`.
  Put the implementation in `Telegram.Linux/Xaml/<Name>Linux.cs`.
- No csproj change is needed for either move: the project sets `EnableDefaultCompileItems`, so
  everything under `Telegram.Linux/` compiles by path. Still build `--no-incremental` after adding or
  removing a file — Uno's generators cache the old file list.

## Graduated — kept here as history, so nobody re-stubs them

- **`ChatBackgroundControl`** — the real one is in the subset (`extra-files.txt`) and paints its
  wallpaper with Skia through `Telegram.Linux/Xaml/ChatBackgroundCanvas.cs`.
- **`ChatStickerButton`** — was a bare `Control` whose `Collapse()`/`Show()` did nothing, so the
  button drew at zero size and the panel could never be opened. Since u-003 the real control is
  compiled (`Telegram/Controls/Chats/ChatStickerButton.cs`, 454 lines, deriving from
  `AnimatedGlyphToggleButton`, which was already in the subset).
- **`ChatRecordButton` / `ChatRecordBar`** — a collapsed button and an inert bar, because there was no
  way to capture audio. Since phase 5 both are the real upstream controls, driven by
  `Telegram.Linux/Platform/ChatRecordEngine.cs`.
- **`StickerPanel`** — a bare `Control` with no `DefaultStyleKey`, which measures 0; that is what
  FALTA §1.4 meant by "the button isn't even visible". Since u-003 the real control is compiled
  (`Telegram/Controls/StickerPanel.xaml` + the four drawers). Keeping the stub is a `CS0263` (partial
  declarations with different base classes — the real one is a `UserControl`) plus a `CS0111` on
  `UpdateChatPermissions`.
- **`ForumView` / `ForumViewItemClickEventArgs`** — the real ones are in the subset
  (`Controls/Views/ForumView.xaml(.cs)`), which is what puts the topic list in the left pane of a
  forum group. The stub only had `ItemClick`/`Scroll`/`UpdateChat`/`UpdateChatTitle`/
  `UpdateChatEmojiStatus`, all of which the real control has, plus the two-argument
  `ForumViewItemClickEventArgs` constructor — the only one anybody calls.
