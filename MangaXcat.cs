using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MangaXcat
{
    internal sealed class MangaItem
    {
        public string Id;
        public string Title;
        public string Status;
        public string CoverUrl;
        public string LastChapter;
        public string ListNames;
        public string SourceName;
        public string SourceUrl;
        public bool MetadataLoaded;

        public string PageUrl
        {
            get { return String.IsNullOrWhiteSpace(SourceUrl)
                ? "https://mangadex.org/title/" + Id
                : SourceUrl; }
        }
    }

    internal sealed class SourceItem
    {
        public string Name;
        public string Url;
        public string Note;
        public string Color;
    }

    internal sealed class ChapterItem
    {
        public string Id;
        public string MangaId;
        public string MangaTitle;
        public string Number;
        public string Title;
        public string Volume;
        public string CoverUrl;
        public string Source;
        public string MaxChapter;
        public bool MangaMetadataLoaded;
        public bool IsRead;
        public int PageNumber;

        public string PageUrl
        {
            get
            {
                string page = PageNumber > 1
                    ? "/" + PageNumber.ToString(CultureInfo.InvariantCulture)
                    : String.Empty;
                return "https://mangadex.org/chapter/" + Id + page;
            }
        }
    }

    internal sealed class BrowserTabState
    {
        public string Url;
        public string Caption;
        public string MangaId;
        public string ChapterId;
    }

    internal sealed class BrowserBookmark
    {
        public string Title;
        public string Url;
    }

    internal sealed class MangaDexTokens
    {
        public string Session;
        public string Refresh;
    }

    internal sealed class MangaDexList
    {
        public string Id;
        public string Name;
        public HashSet<string> MangaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class MangaDexListSyncResult
    {
        public List<MangaDexList> Lists = new List<MangaDexList>();
        public Dictionary<string, HashSet<string>> MangaListMembership =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public List<MangaItem> Manga = new List<MangaItem>();
    }

    internal sealed class MangaWindow : Window
    {
        private static readonly string RootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaShelf");
        private static readonly string FavoritesPath = Path.Combine(RootPath, "favorites.json");
        private static readonly string SavedChaptersPath = Path.Combine(RootPath, "saved-chapters.json");
        private static readonly string ReadingTabsPath = Path.Combine(RootPath, "reading-tabs.json");
        private static readonly string BookmarksPath = Path.Combine(RootPath, "bookmarks.json");
        private static readonly string SettingsPath = Path.Combine(RootPath, "settings.json");
        private static readonly string AccountTokenPath = Path.Combine(RootPath, "mangadex-session.bin");
        private const string MangaDexClientId = "mangadex-frontend-stable";
        private const string MangaDexOAuthTokenEndpoint =
            "https://auth.mangadex.org/realms/mangadex/protocol/openid-connect/token";
        private static readonly string SourcesPath = Path.Combine(RootPath, "sources.json");
        private static readonly string CoverPath = Path.Combine(RootPath, "covers");
        private static readonly string WebViewDataPath = Path.Combine(RootPath, "WebView2");
        private static readonly string AdBlockerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions", "uBOLite");
        private static readonly string[] AdServerHosts =
        {
            "doubleclick.net",
            "googlesyndication.com",
            "googleadservices.com",
            "adservice.google.com",
            "adnxs.com",
            "adsrvr.org",
            "taboola.com",
            "outbrain.com",
            "criteo.com",
            "amazon-adsystem.com",
            "scorecardresearch.com"
        };
        private static readonly Semaphore CoverSlots = new Semaphore(4, 4);
        private const int CoverCacheCapacity = 96;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private readonly object _adBlockerLock = new object();
        private readonly object _accountLock = new object();
        private readonly object _chapterMetadataLock = new object();
        private readonly object _mangaMetadataLock = new object();
        private readonly object _coverCacheLock = new object();
        private readonly Dictionary<string, BitmapImage> _coverCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _coverCacheOrder = new Queue<string>();
        private readonly Dictionary<string, List<Image>> _pendingCoverLoads =
            new Dictionary<string, List<Image>>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _readingTabsSaveTimer =
            new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly DispatcherTimer _savedChaptersSaveTimer =
            new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        private readonly List<MangaItem> _catalog = new List<MangaItem>();
        private readonly List<MangaItem> _favorites = new List<MangaItem>();
        private readonly List<ChapterItem> _chapters = new List<ChapterItem>();
        private readonly List<ChapterItem> _savedChapters = new List<ChapterItem>();
        private readonly List<SourceItem> _sources = new List<SourceItem>();
        private readonly List<BrowserBookmark> _bookmarks = new List<BrowserBookmark>();
        private readonly List<MangaDexList> _mangaDexLists = new List<MangaDexList>();
        private readonly Dictionary<string, HashSet<string>> _mangaDexListMembership =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly WrapPanel _content = new WrapPanel();
        private readonly StackPanel _sourceNav = new StackPanel();
        private readonly TabControl _browserTabs = new TabControl();
        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _subtitle = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBox _search = new TextBox();
        private readonly Button _refresh = new Button();
        private readonly Button _addSource = new Button();
        private readonly Button _accountButton = new Button();
        private readonly Button _importFavorite = new Button();
        private readonly HashSet<string> _chaptersMarkedRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _chapterMetadataRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mangaMetadataRequests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Grid _libraryHost;
        private Grid _readerHost;
        private TextBlock _readerEmpty;
        private MangaItem _currentManga;
        private Task _adBlockerSetupTask;
        private string _sessionToken;
        private string _refreshToken;
        private string _startupPage = "Reader";
        private string _page = "Discover";
        private bool _isLoading;
        private bool _isLoadingChapters;
        private bool _isSyncingReadProgress;
        private bool _isSyncingMangaDexLists;
        private bool _isUpdatingFavoriteLists;
        private bool _isRefreshingFavorites;
        private bool _isImportingFavorite;
        private bool _mangaDexListsLoaded;
        private string _favoriteCategoryId = String.Empty;
        private bool _restoringTabs;
        private int _restoredTabIndex;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        public MangaWindow()
        {
            Title = "MangaXcat";
            Width = 1260;
            Height = 820;
            MinWidth = 920;
            MinHeight = 620;
            Background = Brush("#11121A");
            Foreground = Brush("#F2F1F7");
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SourceInitialized += delegate { SetDarkTitleBar(); };

            _readingTabsSaveTimer.Tick += delegate
            {
                _readingTabsSaveTimer.Stop();
                WriteReadingTabs();
            };
            _savedChaptersSaveTimer.Tick += delegate
            {
                _savedChaptersSaveTimer.Stop();
                WriteSavedChapters();
            };

            LoadFavorites();
            LoadSavedChapters();
            LoadBookmarks();
            foreach (ChapterItem chapter in _savedChapters)
                EnsureMangaMetadata(chapter);
            LoadSettings();
            LoadAccountSession();
            LoadSources();
            Content = BuildShell();
            SetPage(_startupPage);
            Loaded += delegate
            {
                RestoreReadingTabs();
                if (HasMangaDexAccount)
                {
                    SyncReadProgress();
                    SyncMangaDexLists();
                }
            };
            Closing += delegate
            {
                bool saveChapters = _savedChaptersSaveTimer.IsEnabled;
                _readingTabsSaveTimer.Stop();
                _savedChaptersSaveTimer.Stop();
                WriteReadingTabs();
                if (saveChapters) WriteSavedChapters();
            };
        }

        private UIElement BuildShell()
        {
            Grid shell = new Grid();
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid main = new Grid();
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(76) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.Children.Add(BuildTopBar());

            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(30, 25, 30, 28)
            };
            _content.Margin = new Thickness(-6);
            scroll.Content = _content;

            _libraryHost = new Grid();
            _libraryHost.Children.Add(scroll);
            _readerHost = new Grid { Visibility = Visibility.Collapsed };
            _browserTabs.Background = Brush("#11121A");
            _browserTabs.Foreground = Brush("#F2F1F7");
            _browserTabs.BorderBrush = Brush("#2B2C37");
            _browserTabs.SelectionChanged += delegate
            {
                if (_page == "Reader" && _browserTabs.SelectedItem is TabItem)
                    _subtitle.Text = _browserTabs.Items.Count + " open tab" + (_browserTabs.Items.Count == 1 ? "" : "s");
                SaveReadingTabs();
            };
            _readerEmpty = new TextBlock
            {
                Text = "Open a manga or source to start a new in-app tab.",
                Foreground = Brush("#9292A2"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _readerHost.Children.Add(_browserTabs);
            _readerHost.Children.Add(_readerEmpty);

            Grid pageHost = new Grid();
            pageHost.Children.Add(_libraryHost);
            pageHost.Children.Add(_readerHost);
            Grid.SetRow(pageHost, 1);
            main.Children.Add(pageHost);

            shell.Children.Add(BuildSidebar());
            Grid.SetColumn(main, 1);
            shell.Children.Add(main);
            return shell;
        }

        private UIElement BuildSidebar()
        {
            Border sidebar = new Border
            {
                Background = Brush("#161720"),
                BorderBrush = Brush("#2B2C37"),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(17, 24, 17, 16)
            };
            DockPanel dock = new DockPanel();
            StackPanel footer = new StackPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            footer.Children.Add(new Border
            {
                Background = Brush("#20212B"),
                BorderBrush = Brush("#333541"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = "MangaDex catalogue and covers use its public API. Other sites open in app tabs.",
                    Foreground = Brush("#A2A2B1"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 17
                }
            });
            footer.Children.Add(new TextBlock
            {
                Text = "Favourites and chapter bookmarks stay on this device. Read progress can sync with MangaDex.",
                Foreground = Brush("#727382"),
                FontSize = 10,
                Margin = new Thickness(2, 12, 0, 0)
            });
            dock.Children.Add(footer);

            StackPanel top = new StackPanel();
            top.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 0, 0, 29),
                Children =
                {
                    new Border
                    {
                        Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
                        Background = Brush("#C5F36A"), Margin = new Thickness(0, 0, 10, 0),
                        Child = new TextBlock
                        {
                            Text = "M", Foreground = Brush("#191B12"), FontSize = 18,
                            FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center
                        }
                    },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            Label("MangaXcat", 16, "#F5F4FA", FontWeights.Bold),
                            new TextBlock { Text = "YOUR MANGA READING ROOM", FontSize = 8, Foreground = Brush("#8F8F9F"), Margin = new Thickness(0, 2, 0, 0) }
                        }
                    }
                }
            });
            top.Children.Add(SectionLabel("LIBRARY"));
            top.Children.Add(NavButton("Discover", "\uE721", "Discover"));
            top.Children.Add(NavButton("Favourites", "\uE00B", "Favourites"));
            top.Children.Add(NavButton("Saved chapters", "\uE8A4", "Saved chapters"));
            top.Children.Add(NavButton("Reading tabs", "\uE8A7", "Reader"));
            top.Children.Add(SectionLabel("SOURCES"));
            top.Children.Add(_sourceNav);
            RenderSourceNav();
            Button manage = NavButton("Manage sources", "\uE710", "Sources");
            top.Children.Add(manage);
            top.Children.Add(NavButton("Settings", "\uE713", "Settings"));
            dock.Children.Add(top);
            sidebar.Child = dock;
            return sidebar;
        }

        private void RenderSourceNav()
        {
            _sourceNav.Children.Clear();
            foreach (SourceItem source in _sources)
            {
                SourceItem selected = source;
                Button button = NavButton(selected.Name, "\uE774", "Sources");
                button.ToolTip = selected.Url;
                button.Click += delegate { OpenTab(selected.Url, selected.Name); };
                _sourceNav.Children.Add(button);
            }
        }

        private UIElement BuildTopBar()
        {
            Border bar = new Border
            {
                BorderBrush = Brush("#2B2C37"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(30, 0, 30, 0)
            };
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _title.FontSize = 19;
            _title.FontWeight = FontWeights.Bold;
            _title.Foreground = Brush("#F5F4FA");
            _subtitle.FontSize = 11;
            _subtitle.Foreground = Brush("#9191A1");
            _subtitle.Margin = new Thickness(0, 4, 0, 0);
            heading.Children.Add(_title);
            heading.Children.Add(_subtitle);
            grid.Children.Add(heading);

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            _search.Width = 210;
            _search.Height = 36;
            _search.Margin = new Thickness(0, 0, 9, 0);
            _search.Padding = new Thickness(10, 0, 10, 0);
            _search.Background = Brush("#1B1C26");
            _search.Foreground = Brush("#F2F1F7");
            _search.BorderBrush = Brush("#383945");
            _search.BorderThickness = new Thickness(1);
            _search.FontSize = 12;
            _search.ToolTip = "Search loaded titles";
            _search.TextChanged += delegate { RenderCurrentPage(); };
            actions.Children.Add(_search);

            _refresh.Content = "Refresh";
            StyleButton(_refresh, "#272833", "#E6E5ED");
            _refresh.Margin = new Thickness(0, 0, 8, 0);
            _refresh.Click += RefreshClick;
            actions.Children.Add(_refresh);

            _importFavorite.Content = "+ Import link";
            StyleButton(_importFavorite, "#272833", "#E6E5ED");
            _importFavorite.Margin = new Thickness(0, 0, 8, 0);
            _importFavorite.Visibility = Visibility.Collapsed;
            _importFavorite.Click += delegate { ImportFavorite(); };
            actions.Children.Add(_importFavorite);

            _addSource.Content = "+ Add source";
            StyleButton(_addSource, "#C5F36A", "#191B12");
            _addSource.Click += delegate { AddSource(); };
            actions.Children.Add(_addSource);

            _accountButton.Content = HasMangaDexAccount ? "MangaDex connected" : "Connect MangaDex";
            StyleButton(_accountButton, "#272833", "#E6E5ED");
            _accountButton.Margin = new Thickness(9, 0, 0, 0);
            _accountButton.ToolTip = "Sync MangaDex read progress with your account.";
            _accountButton.Click += delegate { AccountButtonClick(); };
            actions.Children.Add(_accountButton);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            bar.Child = grid;
            return bar;
        }

        private Button NavButton(string label, string icon, string page)
        {
            Button button = new Button
            {
                Content = label,
                Tag = page,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 39,
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(10, 0, 8, 0),
                Background = Brush("#161720"),
                Foreground = Brush("#B7B7C5"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12
            };
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = icon,
                Width = 25,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("#AAA1FF"),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14
            });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            button.Click += delegate { SetPage(page); };
            return button;
        }

        private UIElement SectionLabel(string text)
        {
            string icon = text == "LIBRARY" ? "\uE8F1" : "\uE774";
            StackPanel label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(9, 4, 0, 9)
            };
            label.Children.Add(new TextBlock
            {
                Text = icon,
                Foreground = Brush("#777887"),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            label.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brush("#777887"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            return label;
        }

        private void SetPage(string page)
        {
            _page = page;
            _importFavorite.Visibility = Visibility.Collapsed;
            bool showReader = page == "Reader";
            _libraryHost.Visibility = showReader ? Visibility.Collapsed : Visibility.Visible;
            _readerHost.Visibility = showReader ? Visibility.Visible : Visibility.Collapsed;
            _browserTabs.Visibility = _browserTabs.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _readerEmpty.Visibility = _browserTabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (page == "Discover")
            {
                _title.Text = "Discover manga";
                _subtitle.Text = "Popular English-language titles from the MangaDex public API.";
                _search.Visibility = Visibility.Visible;
                _refresh.Visibility = Visibility.Visible;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else if (page == "Favourites")
            {
                _title.Text = "Your favourites";
                _subtitle.Text = FavoritesSummary();
                _search.Visibility = Visibility.Visible;
                _refresh.Visibility = Visibility.Visible;
                _refresh.Content = "Refresh";
                _refresh.IsEnabled = !_isRefreshingFavorites
                    && !_isSyncingMangaDexLists && !_isUpdatingFavoriteLists;
                _importFavorite.Visibility = Visibility.Visible;
                _importFavorite.IsEnabled = !_isImportingFavorite;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else if (page == "Saved chapters")
            {
                _title.Text = "Saved chapters";
                _subtitle.Text = SavedSummary();
                _search.Visibility = Visibility.Visible;
                _refresh.Visibility = Visibility.Collapsed;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else if (page == "Chapters")
            {
                _title.Text = _currentManga == null ? "Chapters" : _currentManga.Title;
                _subtitle.Text = _currentManga == null ? "MangaDex chapter list." : "English chapter list from MangaDex.";
                _search.Visibility = Visibility.Collapsed;
                _refresh.Visibility = Visibility.Visible;
                _refresh.Content = "Reload chapters";
                _importFavorite.Visibility = Visibility.Collapsed;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else if (page == "Reader")
            {
                _title.Text = "Reading tabs";
                _subtitle.Text = _browserTabs.Items.Count + " open tab" + (_browserTabs.Items.Count == 1 ? "" : "s");
                _search.Visibility = Visibility.Collapsed;
                _refresh.Visibility = Visibility.Collapsed;
                _importFavorite.Visibility = Visibility.Collapsed;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else if (page == "Settings")
            {
                _title.Text = "Settings";
                _subtitle.Text = "Choose what MangaXcat opens first.";
                _search.Visibility = Visibility.Collapsed;
                _refresh.Visibility = Visibility.Collapsed;
                _importFavorite.Visibility = Visibility.Collapsed;
                _addSource.Visibility = Visibility.Collapsed;
            }
            else
            {
                _title.Text = "Manga sources";
                _subtitle.Text = "Open a source in an in-app tab. Only MangaDex provides an in-app catalogue.";
                _search.Visibility = Visibility.Collapsed;
                _refresh.Visibility = Visibility.Collapsed;
                _importFavorite.Visibility = Visibility.Collapsed;
                _addSource.Visibility = Visibility.Visible;
            }
            if (page != "Chapters")
                _refresh.Content = "Refresh";
            RenderCurrentPage();
            if (page == "Discover" && _catalog.Count == 0 && !_isLoading)
                LoadCatalog();
        }

        private void SetDarkTitleBar()
        {
            int enabled = 1;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            try
            {
                int result = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
                if (result != 0)
                    result = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                if (result != 0)
                    Debug.WriteLine("Windows did not enable the dark title bar. HRESULT: " + result);
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine("Dark title bar is unavailable: " + ex.Message);
            }
            catch (EntryPointNotFoundException ex)
            {
                Debug.WriteLine("Dark title bar is unavailable: " + ex.Message);
            }
        }

        private void RefreshClick(object sender, RoutedEventArgs e)
        {
            if (_page == "Chapters" && _currentManga != null) LoadChapters(_currentManga);
            else if (_page == "Favourites") RefreshFavorites();
            else LoadCatalog();
        }

        private void RefreshFavorites()
        {
            if (_isRefreshingFavorites || _isSyncingMangaDexLists || _isUpdatingFavoriteLists)
                return;
            if (HasMangaDexAccount)
            {
                SyncMangaDexLists();
                return;
            }
            if (_favorites.Count == 0)
            {
                _status.Text = "There are no favourites to refresh.";
                _status.Foreground = Brush("#9999A8");
                return;
            }

            _isRefreshingFavorites = true;
            _refresh.IsEnabled = false;
            _status.Text = "Refreshing favourite details...";
            _status.Foreground = Brush("#AAA1FF");
            List<MangaItem> favourites = _favorites.ToList();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<MangaItem> updated = favourites.Select(RefreshFavoriteMetadata).ToList();
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _favorites.Clear();
                        _favorites.AddRange(updated);
                        _isRefreshingFavorites = false;
                        _refresh.IsEnabled = _page == "Favourites";
                        SaveFavorites();
                        if (_page == "Favourites") RenderCurrentPage();
                        _status.Text = "Refreshed " + updated.Count + " favourites.";
                        _status.Foreground = Brush("#C5F36A");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isRefreshingFavorites = false;
                        _refresh.IsEnabled = _page == "Favourites";
                        _status.Text = "Could not refresh favourites: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
            });
        }

        private static MangaItem RefreshFavoriteMetadata(MangaItem favorite)
        {
            if (String.IsNullOrWhiteSpace(favorite.SourceUrl))
            {
                MangaItem metadata = FetchMangaMetadata(favorite.Id);
                metadata.ListNames = favorite.ListNames;
                return metadata;
            }

            MangaItem external = FetchExternalMangaMetadata(
                new Uri(favorite.SourceUrl), favorite.SourceName, favorite.Id);
            external.ListNames = favorite.ListNames;
            return external;
        }

        private void ImportFavorite()
        {
            Window dialog = new Window
            {
                Title = "Import favourite",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 430,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("#191A24"),
                Foreground = Brush("#F2F1F7"),
                FontFamily = FontFamily
            };
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(Label("Import a manga link", 17, "#F2F1F7", FontWeights.Bold));
            fields.Children.Add(new TextBlock
            {
                Text = "Paste a manga page from MangaFire, ThunderScans, WEBTOON, or MANGA Plus. MangaXcat will try to read its title and cover.",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12)
            });
            TextBox link = NewDialogField("https://...");
            fields.Children.Add(Label("Manga page URL", 10, "#CECDD7", FontWeights.SemiBold));
            fields.Children.Add(link);
            TextBlock error = new TextBlock
            {
                Foreground = Brush("#FF9393"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            fields.Children.Add(error);
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Button cancel = new Button
            {
                Content = "Cancel",
                Width = 78,
                Height = 31,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#292A35"),
                Foreground = Brush("#E3E2EA"),
                BorderThickness = new Thickness(0)
            };
            cancel.Click += delegate { dialog.DialogResult = false; };
            Button import = new Button
            {
                Content = "Import",
                Width = 86,
                Height = 31,
                Background = Brush("#C5F36A"),
                Foreground = Brush("#191B12"),
                BorderThickness = new Thickness(0)
            };
            import.Click += delegate
            {
                Uri address;
                if (!Uri.TryCreate(link.Text.Trim(), UriKind.Absolute, out address)
                    || GetExternalSourceName(address) == null)
                {
                    error.Text = "Enter an HTTPS manga page from MangaFire, ThunderScans, WEBTOON, or MANGA Plus.";
                    return;
                }
                if (_favorites.Any(item => !String.IsNullOrWhiteSpace(item.SourceUrl)
                    && AreSameWebAddress(item.SourceUrl, address.AbsoluteUri)))
                {
                    error.Text = "This manga link is already in your favourites.";
                    return;
                }
                dialog.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(import);
            fields.Children.Add(buttons);
            dialog.Content = fields;
            dialog.Loaded += delegate { link.Focus(); };
            dialog.ShowDialog();
            if (dialog.DialogResult != true) return;

            Uri importedAddress = new Uri(link.Text.Trim());
            string sourceName = GetExternalSourceName(importedAddress);
            _isImportingFavorite = true;
            _importFavorite.IsEnabled = false;
            _status.Text = "Reading manga details from " + sourceName + "...";
            _status.Foreground = Brush("#AAA1FF");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string id = "external-" + SafeFileName(importedAddress.AbsoluteUri);
                    MangaItem item = FetchExternalMangaMetadata(importedAddress, sourceName, id);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _favorites.RemoveAll(existing => !String.IsNullOrWhiteSpace(existing.SourceUrl)
                            && AreSameWebAddress(existing.SourceUrl, item.SourceUrl));
                        _favorites.Add(item);
                        _isImportingFavorite = false;
                        _importFavorite.IsEnabled = _page == "Favourites";
                        SaveFavorites();
                        if (_page == "Favourites") RenderCurrentPage();
                        _status.Text = "Imported " + item.Title + " from " + item.SourceName + ".";
                        _status.Foreground = Brush("#C5F36A");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isImportingFavorite = false;
                        _importFavorite.IsEnabled = _page == "Favourites";
                        _status.Text = "Could not import this manga link: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
            });
        }

        private static string GetExternalSourceName(Uri address)
        {
            if (address == null || address.Scheme != Uri.UriSchemeHttps)
                return null;
            string host = address.Host;
            if (IsHostOrSubdomain(host, "mangafire.to")) return "MangaFire";
            if (IsHostOrSubdomain(host, "thunderscans.com")) return "ThunderScans";
            if (IsHostOrSubdomain(host, "webtoons.com")) return "WEBTOON";
            if (IsHostOrSubdomain(host, "mangaplus.shueisha.co.jp")) return "MANGA Plus";
            return null;
        }

        private static MangaItem FetchExternalMangaMetadata(Uri address, string sourceName, string id)
        {
            Uri page = address;
            string html = null;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                if (GetExternalSourceName(page) == null)
                    throw new InvalidOperationException("The page redirected outside the supported manga sites.");

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(page);
                request.AllowAutoRedirect = false;
                request.Timeout = 20000;
                request.ReadWriteTimeout = 20000;
                request.UserAgent = "MangaXcatDesktop/1.0";
                request.Accept = "text/html,application/xhtml+xml";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        string location = response.Headers[HttpResponseHeader.Location];
                        if (String.IsNullOrWhiteSpace(location) || redirect == 5)
                            throw new InvalidOperationException("The manga page redirected too many times.");
                        page = new Uri(page, location);
                        continue;
                    }
                    if (status < 200 || status >= 300)
                        throw new InvalidOperationException("The manga site returned HTTP " + status + ".");

                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8, true))
                    {
                        StringBuilder content = new StringBuilder();
                        char[] buffer = new char[8192];
                        int count;
                        while (content.Length < 2 * 1024 * 1024
                            && (count = reader.Read(buffer, 0, Math.Min(buffer.Length, 2 * 1024 * 1024 - content.Length))) > 0)
                            content.Append(buffer, 0, count);
                        html = content.ToString();
                    }
                    break;
                }
            }

            if (String.IsNullOrWhiteSpace(html))
                throw new InvalidOperationException("The manga site returned an empty page.");
            string title = ReadHtmlMeta(html, "og:title");
            if (String.IsNullOrWhiteSpace(title))
            {
                Match titleTag = Regex.Match(html, "<title\\b[^>]*>(.*?)</title\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (titleTag.Success) title = WebUtility.HtmlDecode(titleTag.Groups[1].Value).Trim();
            }
            if (String.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("The page did not provide a manga title.");

            string cover = ReadHtmlMeta(html, "og:image");
            if (String.IsNullOrWhiteSpace(cover)) cover = ReadHtmlMeta(html, "twitter:image");
            Uri coverAddress;
            string coverUrl = Uri.TryCreate(page, cover, out coverAddress)
                && (coverAddress.Scheme == Uri.UriSchemeHttps || coverAddress.Scheme == Uri.UriSchemeHttp)
                ? coverAddress.AbsoluteUri
                : null;
            return new MangaItem
            {
                Id = id,
                Title = title,
                SourceName = sourceName,
                SourceUrl = address.AbsoluteUri,
                CoverUrl = coverUrl,
                MetadataLoaded = true
            };
        }

        private static string ReadHtmlMeta(string html, string name)
        {
            string pattern = "<meta\\b(?=[^>]*(?:property|name)\\s*=\\s*['\"]"
                + Regex.Escape(name)
                + "['\"])(?=[^>]*content\\s*=\\s*(['\"])(.*?)\\1)[^>]*>";
            Match match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? WebUtility.HtmlDecode(match.Groups[2].Value).Trim() : null;
        }

        private static bool IsHostOrSubdomain(string host, string domain)
        {
            return String.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadCatalog()
        {
            if (_isLoading) return;
            _isLoading = true;
            _refresh.IsEnabled = false;
            _status.Text = "Loading MangaDex catalogue...";
            _status.Foreground = Brush("#C5F36A");
            if (_page == "Discover") RenderCurrentPage();

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<MangaItem> result = FetchCatalog();
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _catalog.Clear();
                        _catalog.AddRange(result);
                        _isLoading = false;
                        _refresh.IsEnabled = true;
                        _status.Text = "Loaded " + result.Count + " titles from MangaDex.";
                        _status.Foreground = Brush("#9999A8");
                        RenderCurrentPage();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isLoading = false;
                        _refresh.IsEnabled = true;
                        _status.Text = "Could not load MangaDex: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                        RenderCurrentPage();
                    }));
                }
            });
        }

        private static List<MangaItem> FetchCatalog()
        {
            string url = "https://api.mangadex.org/manga?limit=36&offset=0"
                + "&order%5BfollowedCount%5D=desc"
                + "&contentRating%5B%5D=safe"
                + "&includes%5B%5D=cover_art"
                + "&availableTranslatedLanguage%5B%5D=en";
            string json = RunCurl(url, null);

            Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(json));
            ArrayList entries = AsArray(root.ContainsKey("data") ? root["data"] : null);
            List<MangaItem> result = new List<MangaItem>();
            foreach (object entry in entries)
            {
                Dictionary<string, object> record = AsDictionary(entry);
                Dictionary<string, object> attributes = AsDictionary(Value(record, "attributes"));
                string id = StringValue(Value(record, "id"));
                string title = ReadTitle(attributes);
                string filename = ReadCover(AsArray(Value(record, "relationships")));
                if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title)) continue;
                result.Add(new MangaItem
                {
                    Id = id,
                    Title = title,
                    Status = StringValue(Value(attributes, "status")),
                    CoverUrl = BuildCoverUrl(id, filename),
                    LastChapter = StringValue(Value(attributes, "lastChapter")),
                    MetadataLoaded = true
                });
            }
            return result;
        }

        private static string BuildCoverUrl(string mangaId, string filename)
        {
            return String.IsNullOrWhiteSpace(filename)
                ? null
                : "https://uploads.mangadex.org/covers/" + mangaId + "/" + Uri.EscapeDataString(filename) + ".256.jpg";
        }

        private void LoadChapters(MangaItem manga)
        {
            if (_isLoadingChapters) return;
            _currentManga = manga;
            _chapters.Clear();
            _isLoadingChapters = true;
            _refresh.IsEnabled = false;
            _status.Text = "Loading chapters from MangaDex...";
            _status.Foreground = Brush("#C5F36A");
            SetPage("Chapters");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<ChapterItem> result = FetchChapters(manga);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _chapters.Clear();
                        _chapters.AddRange(result);
                        _isLoadingChapters = false;
                        _refresh.IsEnabled = true;
                        _status.Text = "Loaded " + result.Count + " chapters for " + manga.Title + ".";
                        _status.Foreground = Brush("#9999A8");
                        RenderCurrentPage();
                        if (HasMangaDexAccount) SyncReadProgress();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isLoadingChapters = false;
                        _refresh.IsEnabled = true;
                        _status.Text = "Could not load chapters: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                        RenderCurrentPage();
                    }));
                }
            });
        }

        private static List<ChapterItem> FetchChapters(MangaItem manga)
        {
            string url = "https://api.mangadex.org/manga/" + Uri.EscapeDataString(manga.Id)
                + "/feed?limit=100&translatedLanguage%5B%5D=en"
                + "&order%5Bchapter%5D=desc&order%5Bvolume%5D=desc";
            string json = RunCurl(url, null);
            Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(json));
            ArrayList entries = AsArray(Value(root, "data"));
            List<ChapterItem> result = new List<ChapterItem>();
            foreach (object entry in entries)
            {
                Dictionary<string, object> record = AsDictionary(entry);
                Dictionary<string, object> attributes = AsDictionary(Value(record, "attributes"));
                string id = StringValue(Value(record, "id"));
                if (String.IsNullOrWhiteSpace(id)) continue;
                result.Add(new ChapterItem
                {
                    Id = id,
                    MangaId = manga.Id,
                    MangaTitle = manga.Title,
                    Number = StringValue(Value(attributes, "chapter")),
                    Title = StringValue(Value(attributes, "title")),
                    Volume = StringValue(Value(attributes, "volume")),
                    CoverUrl = manga.CoverUrl,
                    Source = "MangaDex",
                    MaxChapter = manga.LastChapter,
                    MangaMetadataLoaded = manga.MetadataLoaded
                });
            }
            return result;
        }

        private static MangaItem FetchMangaMetadata(string mangaId)
        {
            string path = "/manga/" + Uri.EscapeDataString(mangaId) + "?includes%5B%5D=cover_art";
            Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(
                SendMangaDexRequest("GET", path, null, null)));
            Dictionary<string, object> record = AsDictionary(Value(root, "data"));
            Dictionary<string, object> attributes = AsDictionary(Value(record, "attributes"));
            string id = StringValue(Value(record, "id"));
            string title = ReadTitle(attributes);
            if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("MangaDex did not return this manga's details.");
            return new MangaItem
            {
                Id = id,
                Title = title,
                Status = StringValue(Value(attributes, "status")),
                CoverUrl = BuildCoverUrl(id, ReadCover(AsArray(Value(record, "relationships")))),
                LastChapter = StringValue(Value(attributes, "lastChapter")),
                MetadataLoaded = true
            };
        }

        private static string ReadTitle(Dictionary<string, object> attributes)
        {
            Dictionary<string, object> titles = AsDictionary(Value(attributes, "title"));
            string english = StringValue(Value(titles, "en"));
            if (!String.IsNullOrWhiteSpace(english)) return english;
            foreach (object alternate in AsArray(Value(attributes, "altTitles")))
            {
                Dictionary<string, object> item = AsDictionary(alternate);
                english = StringValue(Value(item, "en"));
                if (!String.IsNullOrWhiteSpace(english)) return english;
            }
            return titles.Values.Select(StringValue).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? "Untitled";
        }

        private static string ReadCover(ArrayList relationships)
        {
            foreach (object value in relationships)
            {
                Dictionary<string, object> relation = AsDictionary(value);
                if (StringValue(Value(relation, "type")) != "cover_art") continue;
                Dictionary<string, object> attributes = AsDictionary(Value(relation, "attributes"));
                return StringValue(Value(attributes, "fileName"));
            }
            return null;
        }

        private void RenderCurrentPage()
        {
            _content.Children.Clear();
            if (_page == "Reader") return;
            if (_page == "Settings")
            {
                RenderSettingsPage();
                return;
            }
            if (_page == "Sources")
            {
                RenderSources();
                return;
            }
            if (_page == "Chapters")
            {
                if (_isLoadingChapters && _chapters.Count == 0)
                {
                    _content.Children.Add(MessageCard("Loading chapters", _status.Text, true));
                    return;
                }
                RenderChapterCards(_chapters, false);
                return;
            }
            if (_page == "Saved chapters")
            {
                RenderChapterCards(_savedChapters, true);
                return;
            }

            IEnumerable<MangaItem> items = _page == "Favourites" ? _favorites : _catalog;
            if (_page == "Favourites")
                RenderFavoriteCategories();
            string query = _search.Text.Trim();
            if (_page == "Favourites" && !String.IsNullOrWhiteSpace(_favoriteCategoryId))
            {
                items = items.Where(item =>
                {
                    HashSet<string> listIds;
                    return _mangaDexListMembership.TryGetValue(item.Id, out listIds)
                        && listIds.Contains(_favoriteCategoryId);
                });
            }
            if (!String.IsNullOrWhiteSpace(query))
                items = items.Where(item => item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            if (_page == "Discover" && _catalog.Count == 0 && _isLoading)
            {
                _content.Children.Add(MessageCard("Connecting to MangaDex", "Loading catalogue and cover artwork. This can take a moment.", true));
                return;
            }
            List<MangaItem> visible = items.ToList();
            if (visible.Count == 0)
            {
                bool selectedCategory = _page == "Favourites" && !String.IsNullOrWhiteSpace(_favoriteCategoryId);
                string heading = _page == "Favourites"
                    ? (selectedCategory ? "No favourites in this category" : "No favourites yet")
                    : "No titles found";
                string detail = _page == "Favourites"
                    ? (selectedCategory ? "Add titles to this MangaDex MDList to see them here."
                        : "Use the heart on a MangaDex title to save it here.")
                    : "Try another search, or refresh the catalogue.";
                if (_page == "Discover" && _catalog.Count == 0 && !String.IsNullOrWhiteSpace(_status.Text))
                    detail = _status.Text;
                _content.Children.Add(MessageCard(heading, detail));
                return;
            }
            foreach (MangaItem item in visible) _content.Children.Add(BuildMangaCard(item));
        }

        private void RenderFavoriteCategories()
        {
            Border card = new Border
            {
                Margin = new Thickness(6),
                Padding = new Thickness(12),
                Background = Brush("#1A1B25"),
                BorderBrush = Brush("#2E2F3B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11)
            };
            StackPanel content = new StackPanel();
            content.Children.Add(Label("CATEGORIES", 9, "#9999A8", FontWeights.Bold));
            WrapPanel buttons = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            AddFavoriteCategoryButton(buttons, "All", String.Empty);
            foreach (MangaDexList list in _mangaDexLists
                .Where(value => !String.IsNullOrWhiteSpace(value.Name))
                .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
                AddFavoriteCategoryButton(buttons, list.Name, list.Id);
            content.Children.Add(buttons);

            if (_mangaDexLists.Count == 0)
            {
                string detail = HasMangaDexAccount
                    ? "No MangaDex MDLists found. Create lists on MangaDex to use them as categories."
                    : "Connect MangaDex to use your MDLists as favourite categories.";
                content.Children.Add(new TextBlock
                {
                    Text = detail,
                    Foreground = Brush("#858594"),
                    FontSize = 10,
                    Margin = new Thickness(0, 7, 0, 0)
                });
            }
            card.Child = content;
            _content.Children.Add(card);
        }

        private void AddFavoriteCategoryButton(Panel panel, string label, string categoryId)
        {
            bool selected = String.Equals(_favoriteCategoryId, categoryId, StringComparison.OrdinalIgnoreCase);
            Button button = new Button
            {
                Content = label,
                MinWidth = 48,
                Height = 28,
                Margin = new Thickness(0, 0, 7, 5),
                Padding = new Thickness(10, 0, 10, 0),
                Background = Brush(selected ? "#C5F36A" : "#272833"),
                Foreground = Brush(selected ? "#191B12" : "#E6E5ED"),
                BorderBrush = Brush(selected ? "#C5F36A" : "#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = String.IsNullOrWhiteSpace(categoryId)
                    ? "Show all favourites"
                    : "Filter favourites by the " + label + " MangaDex MDList."
            };
            button.Click += delegate
            {
                _favoriteCategoryId = categoryId;
                RenderCurrentPage();
            };
            panel.Children.Add(button);
        }

        private string FavoritesSummary()
        {
            return _favorites.Count + " saved titles"
                + (_mangaDexLists.Count == 0 ? ". Changes are stored on this device."
                    : " · " + _mangaDexLists.Count + " MangaDex categories.");
        }

        private int SavedMangaCount()
        {
            return _savedChapters.Select(item => item.MangaId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private string SavedSummary()
        {
            return SavedMangaCount() + " manga · " + _savedChapters.Count
                + " saved chapters. Open its latest saved chapter to continue reading.";
        }

        private void RenderSettingsPage()
        {
            Border card = new Border
            {
                Width = 470,
                Margin = new Thickness(6),
                Padding = new Thickness(20),
                Background = Brush("#1A1B25"),
                BorderBrush = Brush("#2E2F3B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11)
            };
            StackPanel stack = new StackPanel();
            stack.Children.Add(Label("Startup page", 16, "#F2F1F7", FontWeights.SemiBold));
            stack.Children.Add(new TextBlock
            {
                Text = "Choose which section opens when MangaXcat starts. Your choice is saved on this device and takes effect the next time you launch the app.",
                Foreground = Brush("#9999A8"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 12)
            });

            RadioButton readingTabs = new RadioButton
            {
                Content = new TextBlock { Text = "Reading tabs", FontSize = 12, Foreground = Brush("#E6E5ED") },
                GroupName = "StartupPage",
                IsChecked = _startupPage == "Reader",
                Margin = new Thickness(0, 5, 0, 5),
                Cursor = Cursors.Hand
            };
            readingTabs.Checked += delegate { SetStartupPage("Reader"); };
            stack.Children.Add(readingTabs);

            RadioButton savedChapters = new RadioButton
            {
                Content = new TextBlock { Text = "Saved chapters", FontSize = 12, Foreground = Brush("#E6E5ED") },
                GroupName = "StartupPage",
                IsChecked = _startupPage == "Saved chapters",
                Margin = new Thickness(0, 5, 0, 5),
                Cursor = Cursors.Hand
            };
            savedChapters.Checked += delegate { SetStartupPage("Saved chapters"); };
            stack.Children.Add(savedChapters);
            card.Child = stack;
            _content.Children.Add(card);
        }

        private void SetStartupPage(string page)
        {
            if (page != "Reader" && page != "Saved chapters") return;
            string previousPage = _startupPage;
            _startupPage = page;
            if (!SaveSettings())
            {
                _startupPage = previousPage;
                RenderCurrentPage();
                return;
            }
            _subtitle.Text = "Startup page saved: " + (page == "Reader" ? "Reading tabs" : "Saved chapters") + ".";
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                Dictionary<string, object> settings = AsDictionary(
                    Json.DeserializeObject(File.ReadAllText(SettingsPath, Encoding.UTF8)));
                string startupPage = StringValue(Value(settings, "StartupPage"));
                if (startupPage == "Reader" || startupPage == "Saved chapters")
                    _startupPage = startupPage;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved settings could not be read:\n" + ex.Message,
                    "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                string json = Json.Serialize(new { StartupPage = _startupPage });
                File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Settings could not be saved:\n" + ex.Message,
                    "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private UIElement MessageCard(string heading, string detail, bool isLoading = false)
        {
            Border card = new Border
            {
                Background = Brush("#191A24"),
                BorderBrush = Brush("#30313D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22),
                Margin = new Thickness(6),
                Width = 440
            };
            StackPanel stack = new StackPanel();
            stack.Children.Add(Label(heading, 15, "#F2F1F7", FontWeights.SemiBold));
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = Brush("#A0A0AF"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            if (isLoading)
            {
                stack.Children.Add(new ProgressBar
                {
                    IsIndeterminate = true,
                    Height = 5,
                    Margin = new Thickness(0, 13, 0, 0),
                    Background = Brush("#2E2F3B"),
                    Foreground = Brush("#C5F36A"),
                    BorderThickness = new Thickness(0)
                });
            }
            card.Child = stack;
            return card;
        }

        private void RenderChapterCards(IEnumerable<ChapterItem> chapters, bool saved)
        {
            string query = _search.Text.Trim();
            IEnumerable<ChapterItem> items = chapters;
            if (saved)
                items = items.GroupBy(item => item.MangaId, StringComparer.OrdinalIgnoreCase)
                    .Select(HighestSavedChapter);
            if (!String.IsNullOrWhiteSpace(query))
                items = items.Where(item => (item.MangaTitle + " " + item.Number + " " + item.Title)
                    .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            List<ChapterItem> visible = items.ToList();
            if (visible.Count == 0)
            {
                string heading = saved
                    ? "No saved chapters yet"
                    : "No chapters found";
                string detail = saved
                    ? "Open a manga's chapter list and save chapters here for later."
                    : (_isLoadingChapters ? _status.Text : "No English chapters were returned for this manga.");
                if (!String.IsNullOrWhiteSpace(query)) detail = "Try another search.";
                _content.Children.Add(MessageCard(heading, detail));
                return;
            }
            foreach (ChapterItem chapter in visible)
                _content.Children.Add(saved ? BuildSavedChapterCard(chapter) : BuildChapterCard(chapter));
        }

        private static ChapterItem HighestSavedChapter(IEnumerable<ChapterItem> chapters)
        {
            ChapterItem highest = null;
            decimal highestNumber = 0m;
            bool hasNumericChapter = false;
            foreach (ChapterItem chapter in chapters)
            {
                decimal number;
                bool isNumeric = decimal.TryParse(
                    chapter.Number,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out number);
                if (highest == null
                    || (isNumeric && (!hasNumericChapter || number >= highestNumber))
                    || (!isNumeric && !hasNumericChapter))
                {
                    highest = chapter;
                    hasNumericChapter = isNumeric;
                    if (isNumeric) highestNumber = number;
                }
            }
            return highest;
        }

        private UIElement BuildSavedChapterCard(ChapterItem chapter)
        {
            Border card = new Border
            {
                Width = 430,
                Margin = new Thickness(6),
                Padding = new Thickness(12),
                Background = Brush("#1A1B25"),
                BorderBrush = Brush("#2E2F3B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11)
            };
            Grid layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border coverFrame = new Border
            {
                Width = 96,
                Height = 146,
                Background = Brush("#242530"),
                ClipToBounds = true,
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Top
            };
            Image image = new Image { Stretch = Stretch.UniformToFill };
            coverFrame.Child = image;
            if (!String.IsNullOrWhiteSpace(chapter.CoverUrl))
                image.Loaded += delegate { LoadCover(chapter.CoverUrl, image); };
            Grid.SetColumn(coverFrame, 0);
            layout.Children.Add(coverFrame);

            StackPanel details = new StackPanel();
            TextBlock mangaName = Label(chapter.MangaTitle, 13, "#F2F1F7", FontWeights.SemiBold);
            mangaName.TextTrimming = TextTrimming.CharacterEllipsis;
            mangaName.TextWrapping = TextWrapping.Wrap;
            mangaName.MaxHeight = 38;
            details.Children.Add(mangaName);
            details.Children.Add(new TextBlock
            {
                Text = "Source: " + (String.IsNullOrWhiteSpace(chapter.Source) ? "MangaDex" : chapter.Source),
                Foreground = Brush("#9292A2"),
                FontSize = 9,
                Margin = new Thickness(0, 3, 0, 8)
            });

            string chapterLabel = String.IsNullOrWhiteSpace(chapter.Number)
                ? "Oneshot"
                : "Chapter " + chapter.Number;
            if (!String.IsNullOrWhiteSpace(chapter.Volume))
                chapterLabel = "Vol. " + chapter.Volume + "  /  " + chapterLabel;
            details.Children.Add(Label(chapterLabel, 12, "#C5F36A", FontWeights.Bold));
            if (!String.IsNullOrWhiteSpace(chapter.Title))
            {
                details.Children.Add(new TextBlock
                {
                    Text = chapter.Title,
                    Foreground = Brush("#9999A8"),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 3, 0, 6)
                });
            }

            decimal currentChapter = 0m;
            decimal maxChapter = 0m;
            bool hasProgress = decimal.TryParse(
                    chapter.Number,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out currentChapter)
                && decimal.TryParse(
                    chapter.MaxChapter,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out maxChapter)
                && maxChapter > 0;
            double progress = hasProgress
                ? (double)Math.Max(0m, Math.Min(100m, currentChapter / maxChapter * 100m))
                : 0;
            string progressText = hasProgress
                ? String.Format(CultureInfo.InvariantCulture, "{0:0.##} / {1:0.##} chapters", currentChapter, maxChapter)
                : "Progress unavailable";
            details.Children.Add(new TextBlock
            {
                Text = progressText,
                Foreground = Brush("#B9B9C8"),
                FontSize = 9,
                Margin = new Thickness(0, 0, 0, 4)
            });
            details.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = progress,
                Height = 7,
                Background = Brush("#2E2F3B"),
                Foreground = Brush("#C5F36A"),
                BorderThickness = new Thickness(0),
                ToolTip = hasProgress ? progressText : "MangaDex has not provided a chapter maximum."
            });

            DockPanel actions = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            Button delete = new Button
            {
                Content = "Remove",
                Width = 58,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brush("#39262B"),
                Foreground = Brush("#FFB0B5"),
                BorderBrush = Brush("#55343A"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Remove this manga and its saved chapters"
            };
            DockPanel.SetDock(delete, Dock.Right);
            delete.Click += delegate { DeleteSavedChapter(chapter); };
            actions.Children.Add(delete);

            Button read = new Button
            {
                Content = chapter.IsRead ? "Read" : "Unread",
                Width = 58,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brush(chapter.IsRead ? "#263422" : "#272833"),
                Foreground = Brush(chapter.IsRead ? "#C5F36A" : "#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                IsEnabled = HasMangaDexAccount,
                ToolTip = HasMangaDexAccount
                    ? (chapter.IsRead ? "Mark this chapter unread on MangaDex" : "Mark this chapter read on MangaDex")
                    : "Connect a MangaDex account to sync read progress."
            };
            DockPanel.SetDock(read, Dock.Right);
            read.Click += delegate { SetChapterRead(chapter, !chapter.IsRead); };
            actions.Children.Add(read);

            Button open = new Button
            {
                Content = "Open",
                Height = 28,
                Background = Brush("#C5F36A"),
                Foreground = Brush("#191B12"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            open.Click += delegate { OpenTab(chapter.PageUrl, chapter.MangaTitle + " " + chapterLabel, chapter); };
            actions.Children.Add(open);
            details.Children.Add(actions);
            Grid.SetColumn(details, 1);
            layout.Children.Add(details);
            card.Child = layout;
            return card;
        }

        private UIElement BuildChapterCard(ChapterItem chapter)
        {
            Border card = new Border
            {
                Width = 374,
                Margin = new Thickness(6),
                Padding = new Thickness(14),
                Background = Brush("#1A1B25"),
                BorderBrush = Brush("#2E2F3B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11)
            };
            StackPanel stack = new StackPanel();
            string chapterLabel = String.IsNullOrWhiteSpace(chapter.Number) ? "Oneshot" : "Chapter " + chapter.Number;
            if (!String.IsNullOrWhiteSpace(chapter.Volume)) chapterLabel = "Vol. " + chapter.Volume + "  /  " + chapterLabel;
            stack.Children.Add(Label(chapter.MangaTitle, 11, "#C5F36A", FontWeights.SemiBold));
            stack.Children.Add(Label(chapterLabel, 13, "#F2F1F7", FontWeights.Bold));
            stack.Children.Add(new TextBlock
            {
                Text = String.IsNullOrWhiteSpace(chapter.Title) ? " " : chapter.Title,
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 12)
            });

            DockPanel actions = new DockPanel { LastChildFill = true };
            Button save = new Button
            {
                Content = IsChapterSaved(chapter.Id) ? "Saved" : "Save chapter",
                Width = 95,
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brush("#272833"),
                Foreground = Brush("#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Bookmark this chapter; no chapter files are downloaded."
            };
            DockPanel.SetDock(save, Dock.Right);
            save.Click += delegate { ToggleSavedChapter(chapter); };
            actions.Children.Add(save);

            Button read = new Button
            {
                Content = chapter.IsRead ? "Read" : "Mark read",
                Width = 75,
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brush(chapter.IsRead ? "#263422" : "#272833"),
                Foreground = Brush(chapter.IsRead ? "#C5F36A" : "#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                IsEnabled = HasMangaDexAccount,
                ToolTip = HasMangaDexAccount
                    ? (chapter.IsRead ? "Mark this chapter unread on MangaDex" : "Mark this chapter read on MangaDex")
                    : "Connect a MangaDex account to sync read progress."
            };
            DockPanel.SetDock(read, Dock.Right);
            read.Click += delegate { SetChapterRead(chapter, !chapter.IsRead); };
            actions.Children.Add(read);

            Button open = new Button
            {
                Content = "Open in app",
                Height = 30,
                Background = Brush("#C5F36A"),
                Foreground = Brush("#191B12"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            open.Click += delegate { OpenTab(chapter.PageUrl, chapter.MangaTitle + " " + chapterLabel, chapter); };
            actions.Children.Add(open);
            stack.Children.Add(actions);
            card.Child = stack;
            return card;
        }

        private UIElement BuildMangaCard(MangaItem item)
        {
            Border card = new Border
            {
                Width = 174,
                Margin = new Thickness(6),
                Padding = new Thickness(9),
                Background = Brush("#1A1B25"),
                BorderBrush = Brush("#2E2F3B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11)
            };
            StackPanel stack = new StackPanel();
            Grid coverFrame = new Grid
            {
                Height = 205,
                Background = Brush("#242530"),
                ClipToBounds = true
            };
            Image image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center };
            coverFrame.Children.Add(image);
            if (!String.IsNullOrWhiteSpace(item.CoverUrl))
            {
                image.Loaded += delegate { LoadCover(item.CoverUrl, image); };
            }
            stack.Children.Add(coverFrame);
            TextBlock title = Label(item.Title, 12, "#F2F1F7", FontWeights.SemiBold);
            title.Margin = new Thickness(1, 10, 0, 3);
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxHeight = 36;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(title);
            string sourceName = String.IsNullOrWhiteSpace(item.SourceName) ? "MangaDex" : item.SourceName;
            string status = String.IsNullOrWhiteSpace(item.Status) ? sourceName : item.Status;
            string sourceDetails = String.IsNullOrWhiteSpace(item.ListNames)
                ? sourceName + "  /  " + status
                : "MDLists: " + item.ListNames;
            stack.Children.Add(new TextBlock
            {
                Text = sourceDetails,
                Foreground = Brush("#9292A2"),
                FontSize = 9,
                Margin = new Thickness(1, 0, 0, 8),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = sourceDetails
            });

            DockPanel actions = new DockPanel { LastChildFill = true };
            Button favorite = new Button
            {
                Content = IsFavorite(item.Id) ? "♥" : "♡",
                Width = 31,
                Height = 29,
                FontSize = 16,
                Foreground = Brush(IsFavorite(item.Id) ? "#F18F9A" : "#B9B9C8"),
                Background = Brush("#252630"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = IsFavorite(item.Id) ? "Remove from favourites" : "Add to favourites"
            };
            DockPanel.SetDock(favorite, Dock.Right);
            favorite.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(item.SourceUrl))
                    ToggleLocalFavorite(item);
                else
                    ToggleFavorite(item);
            };
            actions.Children.Add(favorite);

            Button chapters = new Button
            {
                Content = "Chapters",
                Height = 29,
                Margin = new Thickness(0, 0, 7, 0),
                FontSize = 10,
                Foreground = Brush("#E6E5ED"),
                Background = Brush("#272833"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            if (String.IsNullOrWhiteSpace(item.SourceUrl))
            {
                chapters.Click += delegate { LoadChapters(item); };
                actions.Children.Add(chapters);
            }
            stack.Children.Add(actions);

            Button open = new Button
            {
                Content = "Open in app",
                Height = 28,
                Margin = new Thickness(0, 7, 0, 0),
                FontSize = 10,
                Foreground = Brush("#191B12"),
                Background = Brush("#C5F36A"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            open.Click += delegate { OpenTab(item.PageUrl, item.Title); };
            stack.Children.Add(open);
            card.Child = stack;
            return card;
        }

        private static string SourceIcon(string name)
        {
            if (String.Equals(name, "MangaDex", StringComparison.OrdinalIgnoreCase))
                return "\uE774";
            if (String.Equals(name, "ThunderScans", StringComparison.OrdinalIgnoreCase))
                return "\uE8A7";
            if (String.Equals(name, "MangaFire", StringComparison.OrdinalIgnoreCase))
                return "\uE8A0";
            return "\uE774";
        }

        private void LoadCover(string url, Image image)
        {
            if (String.IsNullOrWhiteSpace(url)) return;
            lock (_coverCacheLock)
            {
                BitmapImage cached;
                if (_coverCache.TryGetValue(url, out cached))
                {
                    image.Source = cached;
                    return;
                }

                List<Image> pendingImages;
                if (_pendingCoverLoads.TryGetValue(url, out pendingImages))
                {
                    pendingImages.Add(image);
                    return;
                }
                _pendingCoverLoads.Add(url, new List<Image> { image });
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool acquired = false;
                BitmapImage bitmap = null;
                List<Image> waitingImages = null;
                try
                {
                    CoverSlots.WaitOne();
                    acquired = true;
                    Directory.CreateDirectory(CoverPath);
                    string file = Path.Combine(CoverPath, SafeFileName(url) + ".jpg");
                    if (!File.Exists(file))
                    {
                        RunCurl(url, file);
                    }
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(file, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Could not load cover art: " + ex.Message);
                }
                finally
                {
                    if (acquired) CoverSlots.Release();
                    lock (_coverCacheLock)
                    {
                        _pendingCoverLoads.TryGetValue(url, out waitingImages);
                        _pendingCoverLoads.Remove(url);
                        if (bitmap != null)
                        {
                            _coverCache.Add(url, bitmap);
                            _coverCacheOrder.Enqueue(url);
                            while (_coverCache.Count > CoverCacheCapacity)
                                _coverCache.Remove(_coverCacheOrder.Dequeue());
                        }
                    }
                }
                if (bitmap == null || waitingImages == null) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (Image target in waitingImages)
                        target.Source = bitmap;
                }));
            });
        }

        private void RenderSources()
        {
            _content.Children.Add(MessageCard("About source catalogues",
                "Only MangaDex is currently connected to an API. MangaFire and ThunderScans are shortcuts; their catalogues are not scraped."));
            foreach (SourceItem source in _sources)
            {
                Border card = new Border
                {
                    Width = 238,
                    Margin = new Thickness(6),
                    Padding = new Thickness(16),
                    Background = Brush("#1A1B25"),
                    BorderBrush = Brush("#2E2F3B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(11)
                };
                StackPanel stack = new StackPanel();
                Border badge = new Border
                {
                    Width = 36,
                    Height = 36,
                    CornerRadius = new CornerRadius(10),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Brush(source.Color),
                    Child = new TextBlock
                    {
                        Text = SourceIcon(source.Name),
                        Foreground = Brush("#171820"),
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 16,
                        TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                stack.Children.Add(badge);
                stack.Children.Add(Label(source.Name, 14, "#F2F1F7", FontWeights.SemiBold));
                TextBlock note = new TextBlock
                {
                    Text = source.Note,
                    Foreground = Brush("#9999A8"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 9)
                };
                stack.Children.Add(note);
                TextBlock domain = new TextBlock
                {
                    Text = source.Url,
                    Foreground = Brush("#858594"),
                    FontSize = 9,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 11)
                };
                stack.Children.Add(domain);
                Button open = new Button
                {
                    Content = "Open source",
                    Height = 31,
                    Foreground = Brush("#191B12"),
                    Background = Brush("#C5F36A"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                open.Click += delegate { OpenTab(source.Url, source.Name); };
                stack.Children.Add(open);
                card.Child = stack;
                _content.Children.Add(card);
            }
            if (!String.IsNullOrWhiteSpace(_status.Text))
                _content.Children.Add(new TextBlock
                {
                    Text = _status.Text,
                    Foreground = Brush("#9292A2"),
                    FontSize = 10,
                    Margin = new Thickness(9, 12, 0, 0)
                });
        }

        private void AddSource()
        {
            Window dialog = new Window
            {
                Title = "Add a source",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 390,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("#191A24"),
                Foreground = Brush("#F2F1F7"),
                FontFamily = FontFamily
            };
            StackPanel fields = new StackPanel { Margin = new Thickness(20) };
            fields.Children.Add(Label("Add a source shortcut", 17, "#F2F1F7", FontWeights.Bold));
            fields.Children.Add(new TextBlock
            {
                Text = "Custom sources open in your default browser. Catalogues need an authorized API.",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 14)
            });
            TextBox name = NewDialogField("Source name");
            TextBox url = NewDialogField("https://example.com");
            fields.Children.Add(Label("Name", 10, "#CECDD7", FontWeights.SemiBold));
            fields.Children.Add(name);
            fields.Children.Add(Label("Website URL", 10, "#CECDD7", FontWeights.SemiBold));
            fields.Children.Add(url);
            TextBlock error = new TextBlock { Foreground = Brush("#FF9393"), FontSize = 10, Margin = new Thickness(0, 5, 0, 0) };
            fields.Children.Add(error);
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Button cancel = new Button { Content = "Cancel", Width = 78, Height = 31, Margin = new Thickness(0, 0, 8, 0), Background = Brush("#292A35"), Foreground = Brush("#E3E2EA"), BorderThickness = new Thickness(0) };
            cancel.Click += delegate { dialog.DialogResult = false; };
            Button save = new Button { Content = "Add source", Width = 96, Height = 31, Background = Brush("#C5F36A"), Foreground = Brush("#191B12"), BorderThickness = new Thickness(0) };
            save.Click += delegate
            {
                Uri parsed;
                if (String.IsNullOrWhiteSpace(name.Text))
                {
                    error.Text = "Enter a source name.";
                    return;
                }
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
                {
                    error.Text = "Enter a valid http or https website URL.";
                    return;
                }
                if (_sources.Any(source => String.Equals(source.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    error.Text = "A source with that name already exists.";
                    return;
                }
                _sources.Add(new SourceItem
                {
                    Name = name.Text.Trim(),
                    Url = parsed.AbsoluteUri,
                    Note = "Custom source shortcut",
                    Color = "#AAA1FF"
                });
                SaveSources();
                dialog.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            fields.Children.Add(buttons);
            dialog.Content = fields;
            dialog.ShowDialog();
            if (dialog.DialogResult == true)
            {
                RenderSourceNav();
                SetPage("Sources");
            }
        }

        private void LoadBookmarks()
        {
            try
            {
                if (!File.Exists(BookmarksPath)) return;
                ArrayList values = AsArray(Json.DeserializeObject(File.ReadAllText(BookmarksPath, Encoding.UTF8)));
                foreach (object value in values)
                {
                    Dictionary<string, object> row = AsDictionary(value);
                    string title = StringValue(Value(row, "Title"));
                    string url = StringValue(Value(row, "Url"));
                    if (String.IsNullOrWhiteSpace(title) || !IsWebUrl(url)) continue;
                    if (_bookmarks.Any(bookmark => AreSameWebAddress(bookmark.Url, url)))
                        continue;
                    _bookmarks.Add(new BrowserBookmark { Title = title, Url = url });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Browser bookmarks could not be read:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveBookmarks()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                File.WriteAllText(BookmarksPath, Json.Serialize(_bookmarks), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Browser bookmarks could not be saved:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleBookmark(BrowserTabState tabState, string url)
        {
            if (!IsWebUrl(url))
            {
                _status.Text = "Only http and https pages can be bookmarked.";
                _status.Foreground = Brush("#FF9393");
                return;
            }

            BrowserBookmark existing = _bookmarks.FirstOrDefault(bookmark =>
                AreSameWebAddress(bookmark.Url, url));
            if (existing == null)
            {
                Uri address = new Uri(url);
                string title = String.IsNullOrWhiteSpace(tabState.Caption)
                    || String.Equals(tabState.Caption, "New tab", StringComparison.OrdinalIgnoreCase)
                    ? address.Host
                    : tabState.Caption;
                _bookmarks.Add(new BrowserBookmark { Title = title, Url = address.AbsoluteUri });
                _status.Text = "Bookmarked " + title + ".";
                _status.Foreground = Brush("#C5F36A");
            }
            else
            {
                _bookmarks.Remove(existing);
                _status.Text = "Bookmark removed.";
                _status.Foreground = Brush("#9999A8");
            }
            SaveBookmarks();
            RefreshAllBookmarkBars();
        }

        private void RefreshAllBookmarkBars()
        {
            foreach (TabItem tab in _browserTabs.Items.Cast<TabItem>())
            {
                Grid layout = tab.Content as Grid;
                if (layout == null) continue;
                ScrollViewer scroll = layout.Children.OfType<ScrollViewer>().FirstOrDefault();
                if (scroll != null)
                {
                    StackPanel bar = scroll.Content as StackPanel;
                    if (bar != null) RefreshBookmarkBar(bar);
                }
                StackPanel toolbar = layout.Children.OfType<StackPanel>().FirstOrDefault();
                if (toolbar != null)
                {
                    Button bookmarkButton = toolbar.Children.OfType<Button>()
                        .FirstOrDefault(button => button.Name == "BookmarkPage");
                    if (bookmarkButton != null)
                        UpdateBookmarkButton(bookmarkButton, StringValue(bookmarkButton.Tag));
                }
            }
        }

        private void RefreshBookmarkBar(StackPanel bookmarkBar)
        {
            bookmarkBar.Children.Clear();
            if (_bookmarks.Count == 0)
            {
                bookmarkBar.Children.Add(new TextBlock
                {
                    Text = "Bookmarks",
                    Foreground = Brush("#777887"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                });
                return;
            }

            foreach (BrowserBookmark bookmark in _bookmarks.ToArray())
            {
                Button item = new Button
                {
                    Content = bookmark.Title,
                    Tag = bookmark,
                    Height = 25,
                    MaxWidth = 180,
                    Margin = new Thickness(5, 3, 0, 3),
                    Padding = new Thickness(8, 0, 8, 0),
                    Background = Brush("#22232D"),
                    Foreground = Brush("#D8D7E0"),
                    BorderBrush = Brush("#383945"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = bookmark.Url
                };
                item.Click += delegate
                {
                    BrowserBookmark selected = item.Tag as BrowserBookmark;
                    if (selected != null) OpenTab(selected.Url, selected.Title);
                };
                ContextMenu menu = new ContextMenu();
                MenuItem remove = new MenuItem { Header = "Remove bookmark" };
                remove.Click += delegate
                {
                    BrowserBookmark selected = item.Tag as BrowserBookmark;
                    if (selected == null) return;
                    _bookmarks.Remove(selected);
                    SaveBookmarks();
                    RefreshAllBookmarkBars();
                };
                menu.Items.Add(remove);
                item.ContextMenu = menu;
                bookmarkBar.Children.Add(item);
            }
        }

        private void UpdateBookmarkButton(Button button, string url)
        {
            button.Tag = url;
            bool bookmarked = _bookmarks.Any(item =>
                AreSameWebAddress(item.Url, url));
            button.Content = bookmarked ? "★" : "☆";
            button.ToolTip = bookmarked ? "Remove bookmark" : "Bookmark this page";
        }

        private static bool AreSameWebAddress(string first, string second)
        {
            Uri firstUri;
            Uri secondUri;
            return Uri.TryCreate(first, UriKind.Absolute, out firstUri)
                && Uri.TryCreate(second, UriKind.Absolute, out secondUri)
                && Uri.Compare(firstUri, secondUri, UriComponents.HttpRequestUrl,
                    UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
        }

        private WebView2 GetSelectedBrowser()
        {
            TabItem tab = _browserTabs.SelectedItem as TabItem;
            Grid layout = tab == null ? null : tab.Content as Grid;
            return layout == null ? null : layout.Children.OfType<WebView2>().FirstOrDefault();
        }

        private void ManageBrowserExtensions()
        {
            WebView2 browser = GetSelectedBrowser();
            if (browser == null || browser.CoreWebView2 == null)
            {
                MessageBox.Show("Open a browser tab before managing extensions.", "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Window dialog = new Window
            {
                Title = "Browser extensions",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 480,
                Height = 440,
                MinWidth = 420,
                MinHeight = 320,
                Background = Brush("#191A24"),
                Foreground = Brush("#F2F1F7"),
                FontFamily = FontFamily
            };
            Grid content = new Grid { Margin = new Thickness(18) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel heading = new StackPanel();
            heading.Children.Add(Label("Browser extensions", 17, "#F2F1F7", FontWeights.Bold));
            heading.Children.Add(new TextBlock
            {
                Text = "Install compatible Chromium extensions from an unpacked extension folder.",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 12)
            });
            content.Children.Add(heading);

            Button install = new Button
            {
                Content = "Install unpacked extension…",
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 10, 0),
                Background = Brush("#C5F36A"),
                Foreground = Brush("#191B12"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(install, 1);
            content.Children.Add(install);
            StackPanel extensionRows = new StackPanel();
            ScrollViewer extensionScroll = new ScrollViewer
            {
                Content = extensionRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(extensionScroll, 2);
            content.Children.Add(extensionScroll);
            TextBlock extensionStatus = new TextBlock
            {
                Text = "Loading extensions…",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0)
            };
            Grid.SetRow(extensionStatus, 3);
            content.Children.Add(extensionStatus);
            dialog.Content = content;

            install.Click += async delegate
            {
                OpenFileDialog picker = new OpenFileDialog
                {
                    Title = "Select an extension manifest",
                    Filter = "Extension manifest (manifest.json)|manifest.json",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (picker.ShowDialog(dialog) != true) return;
                try
                {
                    await browser.CoreWebView2.Profile.AddBrowserExtensionAsync(Path.GetDirectoryName(picker.FileName));
                    extensionStatus.Text = "Extension installed. Reload affected pages if needed.";
                    extensionStatus.Foreground = Brush("#C5F36A");
                    await RefreshBrowserExtensions(browser, extensionRows, extensionStatus);
                }
                catch (Exception ex)
                {
                    extensionStatus.Text = "Could not install extension: " + ex.Message;
                    extensionStatus.Foreground = Brush("#FF9393");
                }
            };
            dialog.Loaded += async delegate
            {
                await RefreshBrowserExtensions(browser, extensionRows, extensionStatus);
            };
            dialog.ShowDialog();
        }

        private async Task RefreshBrowserExtensions(
            WebView2 browser,
            StackPanel extensionRows,
            TextBlock status)
        {
            try
            {
                IReadOnlyList<CoreWebView2BrowserExtension> extensions =
                    await browser.CoreWebView2.Profile.GetBrowserExtensionsAsync();
                extensionRows.Children.Clear();
                foreach (CoreWebView2BrowserExtension extension in extensions)
                {
                    StackPanel row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 3, 0, 3)
                    };
                    TextBlock name = new TextBlock
                    {
                        Text = extension.Name + (extension.IsEnabled ? " — On" : " — Off"),
                        Foreground = Brush("#E8E7EF"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 245,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = extension.Name
                    };
                    Button toggle = new Button
                    {
                        Content = extension.IsEnabled ? "Disable" : "Enable",
                        Width = 67,
                        Height = 27,
                        Margin = new Thickness(4, 0, 4, 0),
                        Background = Brush("#292A35"),
                        Foreground = Brush("#E3E2EA"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand
                    };
                    toggle.Click += async delegate
                    {
                        try
                        {
                            await extension.EnableAsync(!extension.IsEnabled);
                            await RefreshBrowserExtensions(browser, extensionRows, status);
                        }
                        catch (Exception ex)
                        {
                            status.Text = "Could not update extension: " + ex.Message;
                            status.Foreground = Brush("#FF9393");
                        }
                    };
                    Button remove = new Button
                    {
                        Content = "Remove",
                        Width = 58,
                        Height = 27,
                        Background = Brush("#292A35"),
                        Foreground = Brush("#FF9393"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand
                    };
                    remove.Click += async delegate
                    {
                        if (MessageBox.Show(this, "Remove " + extension.Name + "?", "Browser extensions",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                            return;
                        try
                        {
                            await extension.RemoveAsync();
                            await RefreshBrowserExtensions(browser, extensionRows, status);
                        }
                        catch (Exception ex)
                        {
                            status.Text = "Could not remove extension: " + ex.Message;
                            status.Foreground = Brush("#FF9393");
                        }
                    };
                    row.Children.Add(name);
                    row.Children.Add(toggle);
                    row.Children.Add(remove);
                    extensionRows.Children.Add(row);
                }
                status.Text = extensions.Count == 0
                    ? "No browser extensions installed."
                    : extensions.Count + " extension" + (extensions.Count == 1 ? "" : "s") + " installed.";
                status.Foreground = Brush("#9999A8");
            }
            catch (Exception ex)
            {
                status.Text = "Could not load browser extensions: " + ex.Message;
                status.Foreground = Brush("#FF9393");
            }
        }

        private Task EnsureAdBlocker(CoreWebView2 webView)
        {
            lock (_adBlockerLock)
            {
                if (_adBlockerSetupTask != null) return _adBlockerSetupTask;
                string manifest = Path.Combine(AdBlockerPath, "manifest.json");
                if (!File.Exists(manifest))
                {
                    _adBlockerSetupTask = Task.FromException(new FileNotFoundException(
                        "The bundled uBlock Origin Lite extension is missing.", manifest));
                    return _adBlockerSetupTask;
                }

                _adBlockerSetupTask = webView.Profile.GetBrowserExtensionsAsync().ContinueWith<Task>(extensionsTask =>
                {
                    if (extensionsTask.IsFaulted)
                        return Task.FromException(extensionsTask.Exception);

                    CoreWebView2BrowserExtension installed = extensionsTask.Result.FirstOrDefault(extension =>
                        extension.Name.IndexOf("uBlock Origin Lite", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (installed != null)
                        return installed.IsEnabled ? Task.FromResult(0) : installed.EnableAsync(true);

                    return webView.Profile.AddBrowserExtensionAsync(AdBlockerPath).ContinueWith<Task>(installTask =>
                    {
                        if (installTask.IsFaulted) return Task.FromException(installTask.Exception);
                        return Task.FromResult(0);
                    }).Unwrap();
                }, TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
                return _adBlockerSetupTask;
            }
        }

        private void OpenTab(string url, string caption)
        {
            OpenTab(url, caption, null, null);
        }

        private void OpenTab(string url, string caption, ChapterItem chapter)
        {
            if (chapter != null) SaveChapterAutomatically(chapter);
            OpenTab(url, caption, chapter == null ? null : chapter.MangaId, chapter == null ? null : chapter.Id);
        }

        private void OpenTab(string url, string caption, string mangaId, string chapterId)
        {
            Uri address;
            if (!Uri.TryCreate(url, UriKind.Absolute, out address)
                || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                MessageBox.Show("This source link is not a valid web URL.", "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string requestedChapterId = String.IsNullOrWhiteSpace(chapterId)
                ? GetMangaDexChapterId(address.AbsoluteUri)
                : chapterId;
            TabItem existingTab = _browserTabs.Items.Cast<TabItem>().FirstOrDefault(candidateTab =>
            {
                BrowserTabState state = candidateTab.Tag as BrowserTabState;
                if (state == null) return false;
                string openChapterId = GetMangaDexChapterId(state.Url);
                if (!String.IsNullOrWhiteSpace(requestedChapterId)
                    && String.Equals(openChapterId, requestedChapterId, StringComparison.OrdinalIgnoreCase))
                    return true;
                Uri openAddress;
                return Uri.TryCreate(state.Url, UriKind.Absolute, out openAddress)
                    && Uri.Compare(address, openAddress, UriComponents.HttpRequestUrl,
                        UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
            });
            if (existingTab != null)
            {
                BrowserTabState existingState = existingTab.Tag as BrowserTabState;
                if (existingState != null)
                {
                    if (String.IsNullOrWhiteSpace(existingState.MangaId)) existingState.MangaId = mangaId;
                    if (String.IsNullOrWhiteSpace(existingState.ChapterId))
                        existingState.ChapterId = requestedChapterId;
                }
                SetPage("Reader");
                _browserTabs.SelectedItem = existingTab;
                SaveReadingTabs();
                return;
            }

            SetPage("Reader");
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(33) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = Brush("#1A1B25"),
                Margin = new Thickness(0, 0, 0, 1)
            };
            TextBox addressBox = new TextBox
            {
                Text = address.AbsoluteUri,
                Height = 30,
                Margin = new Thickness(7, 6, 7, 6),
                Padding = new Thickness(9, 0, 9, 0),
                Background = Brush("#11121A"),
                Foreground = Brush("#E8E7EF"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                MinWidth = 230
            };
            Button go = new Button
            {
                Content = "Go",
                Width = 48,
                Height = 30,
                Margin = new Thickness(0, 6, 7, 6),
                Background = Brush("#272833"),
                Foreground = Brush("#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            WebView2 webView = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    AreBrowserExtensionsEnabled = true,
                    UserDataFolder = WebViewDataPath
                }
            };
            Action navigate = delegate
            {
                Uri target;
                string text = addressBox.Text.Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out target)
                    || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp))
                {
                    if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out target)
                        || target.Scheme != Uri.UriSchemeHttps)
                    {
                        MessageBox.Show("Enter a valid http or https address.", "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                addressBox.Text = target.AbsoluteUri;
                if (webView.CoreWebView2 != null) webView.CoreWebView2.Navigate(target.AbsoluteUri);
                else webView.Source = target;
            };
            go.Click += delegate { navigate(); };
            addressBox.KeyDown += delegate(object sender, KeyEventArgs keyArgs)
            {
                if (keyArgs.Key == Key.Enter)
                {
                    navigate();
                    keyArgs.Handled = true;
                }
            };
            Button external = new Button
            {
                Content = "Open browser",
                Height = 30,
                Margin = new Thickness(0, 6, 7, 6),
                Padding = new Thickness(9, 0, 9, 0),
                Background = Brush("#272833"),
                Foreground = Brush("#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            external.Click += delegate { OpenUrl(addressBox.Text); };
            Button bookmark = new Button
            {
                Content = "☆",
                Name = "BookmarkPage",
                Tag = address.AbsoluteUri,
                Width = 34,
                Height = 30,
                Margin = new Thickness(0, 6, 6, 6),
                Background = Brush("#272833"),
                Foreground = Brush("#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Bookmark this page"
            };
            Button extensions = new Button
            {
                Content = "Ext",
                Height = 30,
                Margin = new Thickness(0, 6, 7, 6),
                Padding = new Thickness(8, 0, 8, 0),
                Background = Brush("#272833"),
                Foreground = Brush("#E6E5ED"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Manage browser extensions"
            };
            extensions.Click += delegate { ManageBrowserExtensions(); };
            TextBlock adBlockerStatus = new TextBlock
            {
                Text = "Ad blocker: starting...",
                Foreground = Brush("#AAA1FF"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 8, 0),
                MaxWidth = 112,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            toolbar.Children.Add(addressBox);
            toolbar.Children.Add(go);
            toolbar.Children.Add(external);
            toolbar.Children.Add(bookmark);
            toolbar.Children.Add(extensions);
            toolbar.Children.Add(adBlockerStatus);
            layout.Children.Add(toolbar);
            StackPanel bookmarkBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = Brush("#15161F"),
                Margin = new Thickness(0, 0, 0, 1)
            };
            ScrollViewer bookmarkScroll = new ScrollViewer
            {
                Content = bookmarkBar,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(bookmarkScroll, 1);
            layout.Children.Add(bookmarkScroll);
            RefreshBookmarkBar(bookmarkBar);
            Grid.SetRow(webView, 2);
            layout.Children.Add(webView);
            UpdateBookmarkButton(bookmark, address.AbsoluteUri);

            TabItem tab = new TabItem { Content = layout, ToolTip = address.AbsoluteUri };
            BrowserTabState tabState = new BrowserTabState
            {
                Url = address.AbsoluteUri,
                Caption = String.IsNullOrWhiteSpace(caption) ? "New tab" : caption,
                MangaId = mangaId,
                ChapterId = chapterId
            };
            bookmark.Click += delegate { ToggleBookmark(tabState, addressBox.Text); };
            tab.Tag = tabState;
            StackPanel tabHeader = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock tabTitle = new TextBlock
            {
                Text = tabState.Caption,
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            tabHeader.Children.Add(tabTitle);
            TextBlock previewTitle = new TextBlock
            {
                Text = tabState.Caption,
                Foreground = Brush("#F2F1F7"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 7)
            };
            Image previewImage = new Image
            {
                Width = 300,
                Height = 169,
                Stretch = Stretch.UniformToFill
            };
            TextBlock previewUrl = new TextBlock
            {
                Text = tabState.Url,
                Foreground = Brush("#AAA1FF"),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            };
            TextBlock previewMessage = new TextBlock
            {
                Foreground = Brush("#9999A8"),
                FontSize = 9,
                Margin = new Thickness(0, 5, 0, 0)
            };
            StackPanel previewContent = new StackPanel { Width = 300 };
            previewContent.Children.Add(previewTitle);
            previewContent.Children.Add(previewImage);
            previewContent.Children.Add(previewUrl);
            previewContent.Children.Add(previewMessage);
            ToolTip previewToolTip = new ToolTip
            {
                Content = previewContent,
                Background = Brush("#1A1B25"),
                Foreground = Brush("#F2F1F7"),
                BorderBrush = Brush("#383945"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10)
            };
            previewToolTip.Opened += delegate
            {
                previewTitle.Text = tabState.Caption;
                previewUrl.Text = tabState.Url;
                previewMessage.Text = "Capturing preview...";
                CaptureTabPreview(webView, previewImage, previewMessage);
            };
            tabHeader.ToolTip = previewToolTip;
            ToolTipService.SetInitialShowDelay(tabHeader, 500);
            ToolTipService.SetShowDuration(tabHeader, 10000);
            Button close = new Button
            {
                Content = "x",
                Width = 19,
                Height = 19,
                Padding = new Thickness(0),
                Background = Brush("#292A35"),
                Foreground = Brush("#D8D7E0"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Close tab"
            };
            close.Click += delegate
            {
                _browserTabs.Items.Remove(tab);
                _readerEmpty.Visibility = _browserTabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _browserTabs.Visibility = _browserTabs.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (_browserTabs.Items.Count == 0) _subtitle.Text = "0 open tabs";
                SaveReadingTabs();
            };
            tabHeader.Children.Add(close);
            tab.Header = tabHeader;
            _browserTabs.Items.Add(tab);
            _browserTabs.SelectedItem = tab;
            _browserTabs.Visibility = Visibility.Visible;
            _readerEmpty.Visibility = Visibility.Collapsed;

            webView.CoreWebView2InitializationCompleted += delegate(object initSource, CoreWebView2InitializationCompletedEventArgs initArgs)
            {
                if (!initArgs.IsSuccess)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _status.Text = "The in-app browser could not start: " + initArgs.InitializationException.Message;
                        _status.Foreground = Brush("#FF9393");
                        MessageBox.Show(_status.Text, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
                    }));
                    return;
                }

                webView.CoreWebView2.NewWindowRequested += delegate(object newWindowSource, CoreWebView2NewWindowRequestedEventArgs newWindowArgs)
                {
                    newWindowArgs.Handled = true;
                    OpenTab(newWindowArgs.Uri, "New tab");
                };
                webView.CoreWebView2.DownloadStarting += delegate(object downloadSource, CoreWebView2DownloadStartingEventArgs downloadArgs)
                {
                    downloadArgs.Cancel = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _status.Text = "Downloads are disabled. Chapters can be bookmarked for later.";
                        _status.Foreground = Brush("#9999A8");
                    }));
                };
                webView.CoreWebView2.NavigationStarting += delegate(object navigationSource, CoreWebView2NavigationStartingEventArgs navigationArgs)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        addressBox.Text = navigationArgs.Uri;
                        tab.ToolTip = navigationArgs.Uri;
                        tabState.Url = navigationArgs.Uri;
                        previewUrl.Text = navigationArgs.Uri;
                        UpdateBookmarkButton(bookmark, navigationArgs.Uri);
                        SaveReadingTabs();
                    }));
                };
                webView.CoreWebView2.SourceChanged += delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        string source = webView.CoreWebView2.Source;
                        addressBox.Text = source;
                        tabState.Url = source;
                        previewUrl.Text = source;
                        UpdateBookmarkButton(bookmark, source);
                        SaveReadingTabs();
                        HandleChapterNavigation(source, tab, tabState);
                    }));
                };
                webView.CoreWebView2.DocumentTitleChanged += delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        UpdateBrowserTabMetadata(tab, tabTitle, previewTitle, previewUrl, tabState, webView);
                    }));
                };
                webView.CoreWebView2.NavigationCompleted += delegate(object navigationSource, CoreWebView2NavigationCompletedEventArgs navigationArgs)
                {
                    if (navigationArgs.IsSuccess)
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            UpdateBrowserTabMetadata(tab, tabTitle, previewTitle, previewUrl, tabState, webView);
                            HandleChapterNavigation(webView.CoreWebView2.Source, tab, tabState);
                        }));
                };
                foreach (string host in AdServerHosts)
                    webView.CoreWebView2.AddWebResourceRequestedFilter(
                        "*://*." + host + "/*",
                        CoreWebView2WebResourceContext.All,
                        CoreWebView2WebResourceRequestSourceKinds.All);
                webView.CoreWebView2.WebResourceRequested += delegate(object requestSource, CoreWebView2WebResourceRequestedEventArgs requestArgs)
                {
                    if (!IsAdServer(requestArgs.Request.Uri)) return;
                    requestArgs.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(), 204, "Blocked by MangaXcat", "Content-Type: text/plain\r\n");
                };
                EnsureAdBlocker(webView.CoreWebView2).ContinueWith(delegate(Task installTask)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (installTask.IsFaulted)
                        {
                            Exception error = installTask.Exception == null ? null : installTask.Exception.GetBaseException();
                            _status.Text = "uBlock Origin Lite could not start: " + (error == null ? "unknown error" : error.Message);
                            _status.Foreground = Brush("#FF9393");
                            adBlockerStatus.Text = "Basic ad filter active";
                            adBlockerStatus.Foreground = Brush("#C5F36A");
                            MessageBox.Show(_status.Text, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        else
                        {
                            _status.Text = "uBlock Origin Lite is active in in-app tabs.";
                            _status.Foreground = Brush("#C5F36A");
                            adBlockerStatus.Text = "uBlock Origin Lite: on";
                            adBlockerStatus.Foreground = Brush("#C5F36A");
                        }
                        webView.CoreWebView2.Navigate(address.AbsoluteUri);
                    }));
                });
            };
            try
            {
                webView.EnsureCoreWebView2Async();
                SaveReadingTabs();
            }
            catch (Exception ex)
            {
                _status.Text = "The in-app browser could not start: " + ex.Message;
                _status.Foreground = Brush("#FF9393");
                MessageBox.Show(_status.Text, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string GetMangaDexChapterId(string address)
        {
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri)
                || !(String.Equals(uri.Host, "mangadex.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".mangadex.org", StringComparison.OrdinalIgnoreCase)))
                return null;
            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Guid chapterId;
            return segments.Length >= 2
                && String.Equals(segments[0], "chapter", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out chapterId)
                ? chapterId.ToString()
                : null;
        }

        private static bool IsAdServer(string requestUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out uri)) return false;
            string host = uri.Host;
            return AdServerHosts.Any(domain =>
                String.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        }

        private void RestoreReadingTabs()
        {
            List<BrowserTabState> tabs;
            try
            {
                if (!File.Exists(ReadingTabsPath)) return;
                Dictionary<string, object> record = AsDictionary(Json.DeserializeObject(File.ReadAllText(ReadingTabsPath, Encoding.UTF8)));
                tabs = AsArray(Value(record, "Tabs"))
                    .Cast<object>()
                    .Select(value =>
                    {
                        Dictionary<string, object> row = AsDictionary(value);
                        return new BrowserTabState
                        {
                            Url = StringValue(Value(row, "Url")),
                            Caption = StringValue(Value(row, "Caption")),
                            MangaId = StringValue(Value(row, "MangaId")),
                            ChapterId = StringValue(Value(row, "ChapterId"))
                        };
                    })
                    .Where(tab => IsWebUrl(tab.Url))
                    .ToList();
                int selectedIndex;
                _restoredTabIndex = Int32.TryParse(StringValue(Value(record, "SelectedIndex")), out selectedIndex)
                    ? selectedIndex
                    : 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved reading tabs could not be read:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (tabs.Count == 0) return;
            _restoringTabs = true;
            try
            {
                foreach (BrowserTabState tab in tabs)
                    OpenTab(tab.Url, tab.Caption, tab.MangaId, tab.ChapterId);
                _browserTabs.SelectedIndex = Math.Max(0, Math.Min(_restoredTabIndex, _browserTabs.Items.Count - 1));
            }
            finally
            {
                _restoringTabs = false;
            }
            SetPage(_startupPage);
        }

        private void SaveReadingTabs()
        {
            if (_restoringTabs) return;
            _readingTabsSaveTimer.Stop();
            _readingTabsSaveTimer.Start();
        }

        private void WriteReadingTabs()
        {
            if (_restoringTabs) return;
            try
            {
                BrowserTabState[] tabs = _browserTabs.Items
                    .Cast<TabItem>()
                    .Select(tab => tab.Tag as BrowserTabState)
                    .Where(tab => tab != null && IsWebUrl(tab.Url))
                    .ToArray();
                int selectedIndex = _browserTabs.SelectedIndex;
                Directory.CreateDirectory(RootPath);
                string json = Json.Serialize(new { Tabs = tabs, SelectedIndex = selectedIndex });
                File.WriteAllText(ReadingTabsPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Reading tabs could not be saved:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsWebUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        private void UpdateBrowserTabMetadata(
            TabItem tab,
            TextBlock tabTitle,
            TextBlock previewTitle,
            TextBlock previewUrl,
            BrowserTabState tabState,
            WebView2 webView)
        {
            if (webView.CoreWebView2 == null) return;
            string url = webView.CoreWebView2.Source;
            if (IsWebUrl(url))
            {
                tabState.Url = url;
                previewUrl.Text = url;
                tab.ToolTip = url;
            }
            string title = webView.CoreWebView2.DocumentTitle;
            if (!String.IsNullOrWhiteSpace(title))
                tabState.Caption = UniqueTabCaption(tab, title.Trim());
            else if (IsWebUrl(url))
                tabState.Caption = UniqueTabCaption(tab, new Uri(url).Host);
            tabTitle.Text = tabState.Caption;
            previewTitle.Text = tabState.Caption;
            SaveReadingTabs();
        }

        private string UniqueTabCaption(TabItem tab, string caption)
        {
            HashSet<string> existing = new HashSet<string>(
                _browserTabs.Items.Cast<TabItem>()
                    .Where(other => other != tab)
                    .Select(other => other.Tag as BrowserTabState)
                    .Where(state => state != null && !String.IsNullOrWhiteSpace(state.Caption))
                    .Select(state => state.Caption),
                StringComparer.OrdinalIgnoreCase);
            string unique = caption;
            int suffix = 2;
            while (existing.Contains(unique))
                unique = caption + " (" + suffix++ + ")";
            return unique;
        }

        private async void CaptureTabPreview(WebView2 webView, Image previewImage, TextBlock message)
        {
            if (webView.CoreWebView2 == null)
            {
                message.Text = "The page is still loading.";
                return;
            }

            using (MemoryStream stream = new MemoryStream())
            {
                try
                {
                    await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                    stream.Position = 0;
                    BitmapImage preview = new BitmapImage();
                    preview.BeginInit();
                    preview.CacheOption = BitmapCacheOption.OnLoad;
                    preview.StreamSource = stream;
                    preview.EndInit();
                    preview.Freeze();
                    previewImage.Source = preview;
                    message.Text = String.Empty;
                }
                catch (Exception ex)
                {
                    previewImage.Source = null;
                    message.Text = "Preview unavailable: " + ex.Message;
                }
            }
        }

        private bool HasMangaDexAccount
        {
            get
            {
                lock (_accountLock) return !String.IsNullOrWhiteSpace(_refreshToken);
            }
        }

        private void LoadAccountSession()
        {
            try
            {
                if (!File.Exists(AccountTokenPath)) return;
                byte[] encrypted = File.ReadAllBytes(AccountTokenPath);
                byte[] token = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                _refreshToken = Encoding.UTF8.GetString(token);
                if (String.IsNullOrWhiteSpace(_refreshToken))
                    throw new InvalidDataException("The saved MangaDex refresh token is empty.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("The saved MangaDex connection could not be opened:\n" + ex.Message,
                    "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void StoreRefreshToken(string refreshToken)
        {
            if (String.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("MangaDex did not provide a refresh token.");
            Directory.CreateDirectory(RootPath);
            byte[] token = Encoding.UTF8.GetBytes(refreshToken);
            byte[] encrypted = ProtectedData.Protect(token, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AccountTokenPath, encrypted);
        }

        private void UpdateAccountButton()
        {
            _accountButton.Content = HasMangaDexAccount ? "MangaDex connected" : "Connect MangaDex";
        }

        private void AccountButtonClick()
        {
            if (!HasMangaDexAccount)
            {
                ShowMangaDexLogin();
                return;
            }

            ContextMenu menu = new ContextMenu();
            MenuItem sync = new MenuItem { Header = "Sync read progress" };
            sync.Click += delegate { SyncReadProgress(); };
            MenuItem syncLists = new MenuItem { Header = "Sync MangaDex MDLists" };
            syncLists.Click += delegate { SyncMangaDexLists(); };
            MenuItem disconnect = new MenuItem { Header = "Disconnect MangaDex" };
            disconnect.Click += delegate { DisconnectMangaDex(); };
            menu.Items.Add(sync);
            menu.Items.Add(syncLists);
            menu.Items.Add(disconnect);
            _accountButton.ContextMenu = menu;
            menu.PlacementTarget = _accountButton;
            menu.IsOpen = true;
        }

        private void ShowMangaDexLogin()
        {
            Window dialog = new Window
            {
                Title = "Connect MangaDex",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 900,
                Height = 720,
                MinWidth = 640,
                MinHeight = 520,
                Background = Brush("#191A24"),
                Foreground = Brush("#F2F1F7"),
                FontFamily = FontFamily
            };

            Grid layout = new Grid { Margin = new Thickness(16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            header.Children.Add(Label("Connect your MangaDex account", 17, "#F2F1F7", FontWeights.Bold));
            header.Children.Add(new TextBlock
            {
                Text = "Sign in on MangaDex's official page. Your password is never entered into or stored by MangaXcat.",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            WebView2 loginBrowser = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    AreBrowserExtensionsEnabled = true,
                    UserDataFolder = WebViewDataPath
                }
            };
            Grid.SetRow(loginBrowser, 1);
            layout.Children.Add(loginBrowser);

            StackPanel footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            TextBlock status = new TextBlock
            {
                Text = "Sign in to MangaDex to connect your account.",
                Foreground = Brush("#9999A8"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            footer.Children.Add(status);
            Button cancel = new Button
            {
                Content = "Cancel",
                Width = 78,
                Height = 31,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brush("#292A35"),
                Foreground = Brush("#E3E2EA"),
                BorderThickness = new Thickness(0)
            };
            cancel.Click += delegate { dialog.DialogResult = false; };
            footer.Children.Add(cancel);
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);
            dialog.Content = layout;

            DispatcherTimer pollTimer = null;
            bool checkingTokens = false;
            bool loginError = false;
            loginBrowser.CoreWebView2InitializationCompleted += delegate(
                object sender, CoreWebView2InitializationCompletedEventArgs args)
            {
                if (!args.IsSuccess)
                {
                    loginError = true;
                    status.Text = "The MangaDex sign-in page could not start: "
                        + (args.InitializationException == null ? "unknown WebView2 error" : args.InitializationException.Message);
                    status.Foreground = Brush("#FF9393");
                    return;
                }

                loginBrowser.CoreWebView2.NewWindowRequested += delegate(
                    object newWindowSender, CoreWebView2NewWindowRequestedEventArgs newWindowArgs)
                {
                    newWindowArgs.Handled = true;
                    loginBrowser.CoreWebView2.Navigate(newWindowArgs.Uri);
                };
                loginBrowser.CoreWebView2.NavigationCompleted += delegate
                {
                    if (!loginError)
                    {
                        status.Text = "Complete sign-in on MangaDex; this window will connect automatically.";
                        status.Foreground = Brush("#9999A8");
                    }
                };

                pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                pollTimer.Tick += async delegate
                {
                    if (checkingTokens || loginBrowser.CoreWebView2 == null || dialog.DialogResult.HasValue)
                        return;
                    if (!IsMangaDexWebsite(loginBrowser.CoreWebView2.Source))
                        return;

                    checkingTokens = true;
                    try
                    {
                        MangaDexTokens tokens = await ReadMangaDexTokensAsync(loginBrowser);
                        if (tokens == null) return;

                        pollTimer.Stop();
                        status.Text = "Verifying the MangaDex connection...";
                        status.Foreground = Brush("#AAA1FF");
                        try
                        {
                            await Task.Run(delegate { ConnectMangaDex(tokens); });
                        }
                        catch (Exception ex)
                        {
                            loginError = true;
                            status.Text = "MangaDex sign-in could not be completed: " + ex.Message;
                            status.Foreground = Brush("#FF9393");
                            return;
                        }

                        UpdateAccountButton();
                        _status.Text = "Connected to MangaDex. Read progress will sync.";
                        _status.Foreground = Brush("#C5F36A");
                        dialog.DialogResult = true;
                        SyncReadProgress();
                        SyncMangaDexLists();
                    }
                    catch (Exception ex)
                    {
                        pollTimer.Stop();
                        loginError = true;
                        status.Text = "Could not read the MangaDex sign-in result: " + ex.Message;
                        status.Foreground = Brush("#FF9393");
                    }
                    finally
                    {
                        checkingTokens = false;
                    }
                };
                pollTimer.Start();
                loginBrowser.CoreWebView2.Navigate("https://mangadex.org/login");
            };

            loginBrowser.Loaded += delegate
            {
                try
                {
                    loginBrowser.EnsureCoreWebView2Async();
                }
                catch (Exception ex)
                {
                    loginError = true;
                    status.Text = "The MangaDex sign-in page could not start: " + ex.Message;
                    status.Foreground = Brush("#FF9393");
                }
            };
            dialog.ShowDialog();
        }

        private static bool IsMangaDexWebsite(string address)
        {
            Uri uri;
            return Uri.TryCreate(address, UriKind.Absolute, out uri)
                && String.Equals(uri.Host, "mangadex.org", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<MangaDexTokens> ReadMangaDexTokensAsync(WebView2 browser)
        {
            const string script = @"(() => {
                const key = 'oidc.user:https://auth.mangadex.org/realms/mangadex:mangadex-frontend-stable';
                const saved = localStorage.getItem(key);
                if (!saved) return null;
                const user = JSON.parse(saved);
                if (typeof user.access_token !== 'string' || typeof user.refresh_token !== 'string') return null;
                return { access_token: user.access_token, refresh_token: user.refresh_token };
            })()";
            string result = await browser.CoreWebView2.ExecuteScriptAsync(script);
            Dictionary<string, object> token = AsDictionary(Json.DeserializeObject(result));
            string access = StringValue(Value(token, "access_token"));
            string refresh = StringValue(Value(token, "refresh_token"));
            if (String.IsNullOrWhiteSpace(access) || String.IsNullOrWhiteSpace(refresh))
                return null;
            return new MangaDexTokens { Session = access, Refresh = refresh };
        }

        private void ConnectMangaDex(MangaDexTokens tokens)
        {
            SendMangaDexRequest("GET", "/auth/check", null, tokens.Session);
            StoreRefreshToken(tokens.Refresh);
            lock (_accountLock)
            {
                _sessionToken = tokens.Session;
                _refreshToken = tokens.Refresh;
            }
        }

        private void DisconnectMangaDex()
        {
            try
            {
                lock (_accountLock)
                {
                    if (File.Exists(AccountTokenPath)) File.Delete(AccountTokenPath);
                    _sessionToken = null;
                    _refreshToken = null;
                    _chaptersMarkedRead.Clear();
                }
                _mangaDexLists.Clear();
                _mangaDexListMembership.Clear();
                _mangaDexListsLoaded = false;
                _favoriteCategoryId = String.Empty;
                UpdateAccountButton();
                _status.Text = "MangaDex disconnected. Local saved chapters are unchanged.";
                _status.Foreground = Brush("#9999A8");
                RenderCurrentPage();
            }
            catch (Exception ex)
            {
                MessageBox.Show("MangaDex could not be disconnected:\n" + ex.Message,
                    "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static MangaDexTokens ParseMangaDexTokens(string response, string fallbackRefreshToken)
        {
            Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(response));
            string session = StringValue(Value(root, "access_token"));
            string refresh = StringValue(Value(root, "refresh_token"));
            if (String.IsNullOrWhiteSpace(refresh)) refresh = fallbackRefreshToken;
            if (String.IsNullOrWhiteSpace(session) || String.IsNullOrWhiteSpace(refresh))
                throw new InvalidOperationException("MangaDex did not return valid session tokens.");
            return new MangaDexTokens { Session = session, Refresh = refresh };
        }

        private static string SendMangaDexRequest(string method, string path, object body, string sessionToken)
        {
            string payload = body == null ? null : ToAsciiJson(Json.Serialize(body));
            return SendCurlRequest(
                "https://api.mangadex.org" + path,
                method,
                payload,
                body == null ? null : "application/json",
                sessionToken);
        }

        private static string SendMangaDexFormRequest(string payload)
        {
            return SendCurlRequest(
                MangaDexOAuthTokenEndpoint,
                "POST",
                payload,
                "application/x-www-form-urlencoded",
                null);
        }

        private static string SendCurlRequest(string url, string method, string payload, string contentType, string sessionToken)
        {
            string curlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (!File.Exists(curlPath))
                throw new InvalidOperationException("The Windows curl.exe utility is required to connect to MangaDex.");

            StringBuilder config = new StringBuilder();
            config.AppendLine("silent");
            config.AppendLine("show-error");
            config.AppendLine("fail-with-body");
            config.AppendLine("location");
            config.AppendLine("max-time = 20");
            config.AppendLine("user-agent = \"MangaXcatDesktop/1.0\"");
            config.AppendLine("url = \"" + EscapeCurlConfigValue(url) + "\"");
            config.AppendLine("request = \"" + EscapeCurlConfigValue(method) + "\"");
            config.AppendLine("header = \"Accept: application/json\"");
            if (!String.IsNullOrWhiteSpace(sessionToken))
                config.AppendLine("header = \"Authorization: Bearer " + EscapeCurlConfigValue(sessionToken) + "\"");
            if (!String.IsNullOrWhiteSpace(contentType))
                config.AppendLine("header = \"Content-Type: " + EscapeCurlConfigValue(contentType) + "\"");
            if (payload != null)
            {
                config.AppendLine("data = \"" + EscapeCurlConfigValue(payload) + "\"");
            }
            config.AppendLine("write-out = \"%{http_code}\"");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = "--config -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start))
            {
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                process.StandardInput.Write(config.ToString());
                process.StandardInput.Close();
                process.WaitForExit();
                string output = outputTask.Result;
                string error = errorTask.Result;
                int statusCode;
                string responseBody = output.Length >= 3 ? output.Substring(0, output.Length - 3) : String.Empty;
                if (!Int32.TryParse(output.Length >= 3 ? output.Substring(output.Length - 3) : String.Empty, out statusCode))
                    statusCode = 0;
                if (statusCode >= 400)
                    throw new MangaDexHttpException((HttpStatusCode)statusCode, ApiErrorMessage(responseBody));
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(error)
                        ? "The MangaDex request failed (curl exit code " + process.ExitCode + ")."
                        : error.Trim());
                return responseBody;
            }
        }

        private static string EscapeCurlConfigValue(string value)
        {
            return value.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static string ToAsciiJson(string value)
        {
            StringBuilder result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (character > 127)
                    result.Append("\\u").Append(((int)character).ToString("x4"));
                else
                    result.Append(character);
            }
            return result.ToString();
        }

        private static string ApiErrorMessage(string responseBody)
        {
            Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(responseBody));
            string message = StringValue(Value(root, "message"));
            if (String.IsNullOrWhiteSpace(message))
                message = StringValue(Value(root, "error_description"));
            if (String.IsNullOrWhiteSpace(message))
                message = StringValue(Value(root, "error"));
            ArrayList errors = AsArray(Value(root, "errors"));
            if (errors.Count > 0)
            {
                Dictionary<string, object> first = AsDictionary(errors[0]);
                string detail = StringValue(Value(first, "detail"));
                if (!String.IsNullOrWhiteSpace(detail)) message = detail;
                else if (String.IsNullOrWhiteSpace(message)) message = StringValue(Value(first, "title"));
            }
            return message;
        }

        private sealed class MangaDexHttpException : Exception
        {
            public readonly HttpStatusCode StatusCode;

            public MangaDexHttpException(HttpStatusCode statusCode, string message)
                : base(String.IsNullOrWhiteSpace(message)
                    ? "MangaDex returned HTTP " + (int)statusCode + "."
                    : "MangaDex returned HTTP " + (int)statusCode + ": " + message)
            {
                StatusCode = statusCode;
            }
        }

        private string AuthenticatedMangaDexRequest(string method, string path, object body)
        {
            lock (_accountLock)
            {
                if (String.IsNullOrWhiteSpace(_refreshToken))
                    throw new InvalidOperationException("Connect a MangaDex account to sync read progress.");
                if (String.IsNullOrWhiteSpace(_sessionToken)) RefreshMangaDexSession();
                try
                {
                    return SendMangaDexRequest(method, path, body, _sessionToken);
                }
                catch (MangaDexHttpException ex)
                {
                    if (ex.StatusCode != HttpStatusCode.Unauthorized) throw;
                    _sessionToken = null;
                    RefreshMangaDexSession();
                    return SendMangaDexRequest(method, path, body, _sessionToken);
                }
            }
        }

        private void RefreshMangaDexSession()
        {
            string payload = "grant_type=refresh_token&client_id=" + Uri.EscapeDataString(MangaDexClientId)
                + "&refresh_token=" + Uri.EscapeDataString(_refreshToken);
            string response = SendMangaDexFormRequest(payload);
            MangaDexTokens tokens = ParseMangaDexTokens(response, _refreshToken);
            StoreRefreshToken(tokens.Refresh);
            _sessionToken = tokens.Session;
            _refreshToken = tokens.Refresh;
        }

        private Dictionary<string, HashSet<string>> FetchReadMarkers(List<string> mangaIds)
        {
            Dictionary<string, HashSet<string>> result = new Dictionary<string, HashSet<string>>();
            for (int offset = 0; offset < mangaIds.Count; offset += 100)
            {
                List<string> batch = mangaIds.Skip(offset).Take(100).ToList();
                string query = String.Join("&ids%5B%5D=", batch.Select(Uri.EscapeDataString));
                string path = "/manga/read?ids%5B%5D=" + query + "&grouped=true";
                Dictionary<string, object> root = AsDictionary(
                    Json.DeserializeObject(AuthenticatedMangaDexRequest("GET", path, null)));
                Dictionary<string, object> data = AsDictionary(Value(root, "data"));
                foreach (string mangaId in batch)
                {
                    result[mangaId] = new HashSet<string>(
                        AsArray(Value(data, mangaId)).Cast<object>().Select(StringValue)
                            .Where(id => !String.IsNullOrWhiteSpace(id)),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
            return result;
        }

        private void SyncReadProgress()
        {
            if (!HasMangaDexAccount) return;
            if (_isSyncingReadProgress) return;
            List<ChapterItem> chapters = _savedChapters.Concat(_chapters)
                .GroupBy(chapter => chapter.Id)
                .Select(group => group.First())
                .ToList();
            if (chapters.Count == 0)
            {
                _status.Text = "Open or save MangaDex chapters before syncing read progress.";
                _status.Foreground = Brush("#9999A8");
                return;
            }

            _isSyncingReadProgress = true;
            _status.Text = "Syncing read progress from MangaDex...";
            _status.Foreground = Brush("#AAA1FF");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Dictionary<string, HashSet<string>> markers = FetchReadMarkers(
                        chapters.Select(chapter => chapter.MangaId).Distinct().ToList());
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (ChapterItem chapter in _savedChapters.Concat(_chapters))
                        {
                            HashSet<string> readIds;
                            chapter.IsRead = markers.TryGetValue(chapter.MangaId, out readIds) && readIds.Contains(chapter.Id);
                            lock (_accountLock)
                            {
                                if (chapter.IsRead) _chaptersMarkedRead.Add(chapter.Id);
                                else _chaptersMarkedRead.Remove(chapter.Id);
                            }
                        }
                        SaveSavedChapters();
                        _isSyncingReadProgress = false;
                        _status.Text = "Read progress synced with MangaDex.";
                        _status.Foreground = Brush("#C5F36A");
                        RenderCurrentPage();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isSyncingReadProgress = false;
                        _status.Text = "Could not sync MangaDex read progress: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                        RenderCurrentPage();
                    }));
                }
            });
        }

        private void SyncMangaDexLists()
        {
            if (!HasMangaDexAccount || _isSyncingMangaDexLists || _isUpdatingFavoriteLists)
                return;
            _isSyncingMangaDexLists = true;
            if (_page == "Favourites") _refresh.IsEnabled = false;
            List<MangaItem> localFavorites = _favorites
                .Where(item => String.IsNullOrWhiteSpace(item.SourceUrl))
                .ToList();
            List<MangaItem> externalFavorites = _favorites
                .Where(item => !String.IsNullOrWhiteSpace(item.SourceUrl))
                .ToList();
            _status.Text = "Syncing MangaDex MDLists...";
            _status.Foreground = Brush("#AAA1FF");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    MangaDexListSyncResult result = FetchMangaDexListSync(localFavorites);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (!HasMangaDexAccount)
                        {
                            _isSyncingMangaDexLists = false;
                            return;
                        }
                        _mangaDexLists.Clear();
                        _mangaDexLists.AddRange(result.Lists);
                        if (!_mangaDexLists.Any(list => String.Equals(
                            list.Id, _favoriteCategoryId, StringComparison.OrdinalIgnoreCase)))
                            _favoriteCategoryId = String.Empty;
                        _mangaDexListMembership.Clear();
                        foreach (KeyValuePair<string, HashSet<string>> entry in result.MangaListMembership)
                            _mangaDexListMembership.Add(entry.Key, entry.Value);
                        _mangaDexListsLoaded = true;
                        if (result.Lists.Count > 0 || _favorites.Count == 0)
                        {
                            _favorites.Clear();
                            _favorites.AddRange(result.Manga);
                            _favorites.AddRange(externalFavorites);
                        }
                        SaveFavorites();
                        if (_page == "Favourites") RenderCurrentPage();
                        _isSyncingMangaDexLists = false;
                        if (_page == "Favourites") _refresh.IsEnabled = true;
                        _status.Text = result.Lists.Count == 0
                            ? "No MangaDex MDLists found. Favourites are stored locally."
                            : "Synced " + result.Manga.Count + " titles from "
                                + result.Lists.Count + " MangaDex MDLists.";
                        _status.Foreground = Brush("#C5F36A");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isSyncingMangaDexLists = false;
                        if (_page == "Favourites") _refresh.IsEnabled = true;
                        if (!HasMangaDexAccount) return;
                        _mangaDexListsLoaded = false;
                        _status.Text = "Could not sync MangaDex MDLists: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
            });
        }

        private MangaDexListSyncResult FetchMangaDexListSync(List<MangaItem> localFavorites)
        {
            List<MangaDexList> lists = new List<MangaDexList>();
            int offset = 0;
            while (true)
            {
                string path = "/user/list?limit=100&offset=" + offset.ToString(CultureInfo.InvariantCulture);
                Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(
                    AuthenticatedMangaDexRequest("GET", path, null)));
                ArrayList entries = AsArray(Value(root, "data"));
                foreach (object value in entries)
                {
                    Dictionary<string, object> record = AsDictionary(value);
                    string id = StringValue(Value(record, "id"));
                    if (String.IsNullOrWhiteSpace(id)) continue;
                    lists.Add(new MangaDexList
                    {
                        Id = id,
                        Name = StringValue(Value(AsDictionary(Value(record, "attributes")), "name"))
                    });
                }
                offset += entries.Count;
                int total = IntegerValue(Value(root, "total"));
                if (entries.Count == 0 || total == 0 || offset >= total) break;
            }

            foreach (MangaDexList list in lists)
            {
                string path = "/list/" + Uri.EscapeDataString(list.Id);
                Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(
                    AuthenticatedMangaDexRequest("GET", path, null)));
                Dictionary<string, object> record = AsDictionary(Value(root, "data"));
                foreach (object value in AsArray(Value(record, "relationships")))
                {
                    Dictionary<string, object> relation = AsDictionary(value);
                    if (StringValue(Value(relation, "type")) != "manga") continue;
                    string mangaId = StringValue(Value(relation, "id"));
                    if (!String.IsNullOrWhiteSpace(mangaId)) list.MangaIds.Add(mangaId);
                }
            }

            if (lists.Count > 0)
            {
                foreach (MangaItem favorite in localFavorites)
                {
                    if (!String.IsNullOrWhiteSpace(favorite.ListNames)) continue;
                    foreach (MangaDexList list in lists)
                    {
                        if (!list.MangaIds.Add(favorite.Id)) continue;
                        string path = "/manga/" + Uri.EscapeDataString(favorite.Id)
                            + "/list/" + Uri.EscapeDataString(list.Id);
                        AuthenticatedMangaDexRequest("POST", path, null);
                    }
                }
            }

            Dictionary<string, HashSet<string>> membership =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (MangaDexList list in lists)
            {
                foreach (string mangaId in list.MangaIds)
                {
                    HashSet<string> listIds;
                    if (!membership.TryGetValue(mangaId, out listIds))
                    {
                        listIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        membership.Add(mangaId, listIds);
                    }
                    listIds.Add(list.Id);
                }
            }

            Dictionary<string, MangaDexList> listsById = lists.ToDictionary(
                list => list.Id,
                StringComparer.OrdinalIgnoreCase);
            List<MangaItem> manga = new List<MangaItem>();
            List<string> mangaIds = membership.Keys.ToList();
            for (int index = 0; index < mangaIds.Count; index += 100)
            {
                List<string> batch = mangaIds.Skip(index).Take(100).ToList();
                string query = String.Join("&ids%5B%5D=", batch.Select(Uri.EscapeDataString));
                string path = "/manga?ids%5B%5D=" + query + "&includes%5B%5D=cover_art";
                Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(
                    AuthenticatedMangaDexRequest("GET", path, null)));
                foreach (object value in AsArray(Value(root, "data")))
                {
                    Dictionary<string, object> record = AsDictionary(value);
                    Dictionary<string, object> attributes = AsDictionary(Value(record, "attributes"));
                    string id = StringValue(Value(record, "id"));
                    string title = ReadTitle(attributes);
                    if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title)) continue;
                    string filename = ReadCover(AsArray(Value(record, "relationships")));
                    HashSet<string> itemLists;
                    membership.TryGetValue(id, out itemLists);
                    string listNames = String.Join(", ", (itemLists ?? new HashSet<string>())
                        .Select(listId => listsById[listId].Name)
                        .Where(name => !String.IsNullOrWhiteSpace(name))
                        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
                    manga.Add(new MangaItem
                    {
                        Id = id,
                        Title = title,
                        Status = StringValue(Value(attributes, "status")),
                        CoverUrl = BuildCoverUrl(id, filename),
                        LastChapter = StringValue(Value(attributes, "lastChapter")),
                        ListNames = listNames,
                        MetadataLoaded = true
                    });
                }
            }

            return new MangaDexListSyncResult
            {
                Lists = lists,
                MangaListMembership = membership,
                Manga = manga.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
            };
        }

        private void SetChapterRead(ChapterItem chapter, bool isRead)
        {
            if (!HasMangaDexAccount)
            {
                ShowMangaDexLogin();
                return;
            }
            QueueSetChapterRead(chapter.MangaId, chapter.Id, isRead);
        }

        private void QueueSetChapterRead(string mangaId, string chapterId, bool isRead)
        {
            lock (_accountLock)
            {
                if (isRead && !_chaptersMarkedRead.Add(chapterId)) return;
                if (!isRead) _chaptersMarkedRead.Remove(chapterId);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body[isRead ? "chapterIdsRead" : "chapterIdsUnread"] = new[] { chapterId };
                    string path = "/manga/" + Uri.EscapeDataString(mangaId) + "/read"
                        + (isRead ? "?updateHistory=true" : String.Empty);
                    AuthenticatedMangaDexRequest("POST", path, body);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (ChapterItem chapter in _savedChapters.Concat(_chapters).Where(item => item.Id == chapterId))
                            chapter.IsRead = isRead;
                        SaveSavedChapters();
                        _status.Text = isRead ? "Chapter marked read on MangaDex." : "Chapter marked unread on MangaDex.";
                        _status.Foreground = Brush("#C5F36A");
                        RenderCurrentPage();
                    }));
                }
                catch (Exception ex)
                {
                    lock (_accountLock)
                    {
                        if (isRead) _chaptersMarkedRead.Remove(chapterId);
                        else _chaptersMarkedRead.Add(chapterId);
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _status.Text = "Could not update MangaDex read progress: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                        RenderCurrentPage();
                    }));
                }
            });
        }

        private void HandleChapterNavigation(string url, TabItem tab, BrowserTabState tabState)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || !(String.Equals(uri.Host, "mangadex.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".mangadex.org", StringComparison.OrdinalIgnoreCase)))
            {
                ClearChapterTabState(tabState);
                return;
            }
            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Guid chapterGuid;
            if (segments.Length < 2 || !String.Equals(segments[0], "chapter", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(segments[1], out chapterGuid))
            {
                ClearChapterTabState(tabState);
                return;
            }

            string chapterId = chapterGuid.ToString();
            int pageNumber = GetChapterPageNumber(uri.AbsoluteUri, chapterId);
            bool knownManga = String.Equals(tabState.ChapterId, chapterId, StringComparison.OrdinalIgnoreCase)
                && !String.IsNullOrWhiteSpace(tabState.MangaId);
            tabState.ChapterId = chapterId;
            tab.ToolTip = uri.AbsoluteUri;
            SaveReadingTabs();
            UpdateSavedChapterPage(chapterId, pageNumber);

            ChapterItem chapter = _chapters.Concat(_savedChapters)
                .FirstOrDefault(item => String.Equals(item.Id, chapterId, StringComparison.OrdinalIgnoreCase));
            if (chapter != null)
            {
                chapter.PageNumber = pageNumber;
                tabState.MangaId = chapter.MangaId;
                SaveReadingTabs();
                SaveChapterAutomatically(chapter);
                if (HasMangaDexAccount) QueueSetChapterRead(chapter.MangaId, chapterId, true);
                return;
            }

            lock (_chapterMetadataLock)
            {
                if (!_chapterMetadataRequests.Add(chapterId)) return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string path = "/chapter/" + Uri.EscapeDataString(chapterId) + "?includes%5B%5D=manga";
                    Dictionary<string, object> root = AsDictionary(Json.DeserializeObject(
                        SendMangaDexRequest("GET", path, null, null)));
                    Dictionary<string, object> record = AsDictionary(Value(root, "data"));
                    Dictionary<string, object> attributes = AsDictionary(Value(record, "attributes"));
                    string mangaId = AsArray(Value(record, "relationships"))
                        .Cast<object>()
                        .Select(AsDictionary)
                        .Where(relation => StringValue(Value(relation, "type")) == "manga")
                        .Select(relation => StringValue(Value(relation, "id")))
                        .FirstOrDefault(id => !String.IsNullOrWhiteSpace(id));
                    if (String.IsNullOrWhiteSpace(mangaId) && knownManga)
                        mangaId = tabState.MangaId;
                    if (String.IsNullOrWhiteSpace(mangaId))
                        throw new InvalidOperationException("MangaDex did not return the chapter's manga ID.");
                    Dictionary<string, object> mangaRecord = AsArray(Value(root, "included"))
                        .Cast<object>()
                        .Select(AsDictionary)
                        .FirstOrDefault(item => StringValue(Value(item, "type")) == "manga");
                    string mangaTitle = mangaRecord == null
                        ? "MangaDex chapter"
                        : ReadTitle(AsDictionary(Value(mangaRecord, "attributes")));
                    Dictionary<string, object> mangaAttributes = AsDictionary(Value(mangaRecord, "attributes"));
                    ChapterItem resolvedChapter = new ChapterItem
                    {
                        Id = chapterId,
                        MangaId = mangaId,
                        MangaTitle = mangaTitle,
                        Number = StringValue(Value(attributes, "chapter")),
                        Title = StringValue(Value(attributes, "title")),
                        Volume = StringValue(Value(attributes, "volume")),
                        Source = "MangaDex",
                        MaxChapter = StringValue(Value(mangaAttributes, "lastChapter")),
                        PageNumber = pageNumber
                    };
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (String.Equals(tabState.ChapterId, chapterId, StringComparison.OrdinalIgnoreCase))
                        {
                            tabState.MangaId = mangaId;
                            resolvedChapter.PageNumber = GetChapterPageNumber(tabState.Url, chapterId);
                            SaveReadingTabs();
                        }
                        SaveChapterAutomatically(resolvedChapter);
                    }));
                    if (HasMangaDexAccount) QueueSetChapterRead(mangaId, chapterId, true);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _status.Text = "Could not automatically save this MangaDex chapter: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
                finally
                {
                    lock (_chapterMetadataLock) _chapterMetadataRequests.Remove(chapterId);
                }
            });
        }

        private void ClearChapterTabState(BrowserTabState tabState)
        {
            if (String.IsNullOrWhiteSpace(tabState.ChapterId) && String.IsNullOrWhiteSpace(tabState.MangaId))
                return;
            tabState.ChapterId = null;
            tabState.MangaId = null;
            SaveReadingTabs();
        }

        private static int GetChapterPageNumber(string url, string chapterId)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return 1;
            string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int page;
            if (segments.Length >= 3
                && String.Equals(segments[0], "chapter", StringComparison.OrdinalIgnoreCase)
                && String.Equals(segments[1], chapterId, StringComparison.OrdinalIgnoreCase)
                && Int32.TryParse(segments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out page)
                && page > 0)
                return page;
            return 1;
        }

        private void UpdateSavedChapterPage(string chapterId, int pageNumber)
        {
            foreach (ChapterItem chapter in _chapters.Concat(_savedChapters)
                .Where(item => String.Equals(item.Id, chapterId, StringComparison.OrdinalIgnoreCase)))
            {
                if (chapter.PageNumber == pageNumber) continue;
                chapter.PageNumber = pageNumber;
            }
        }

        private TextBox NewDialogField(string placeholder)
        {
            return new TextBox
            {
                Height = 34,
                Margin = new Thickness(0, 5, 0, 12),
                Padding = new Thickness(9, 0, 9, 0),
                Background = Brush("#11121A"),
                Foreground = Brush("#F2F1F7"),
                BorderBrush = Brush("#3A3B47"),
                BorderThickness = new Thickness(1),
                ToolTip = placeholder
            };
        }

        private void ToggleFavorite(MangaItem item)
        {
            if (!HasMangaDexAccount)
            {
                ToggleLocalFavorite(item);
                return;
            }
            if (_isSyncingMangaDexLists || _isUpdatingFavoriteLists)
            {
                _status.Text = "Wait for the current MangaDex list sync to finish.";
                _status.Foreground = Brush("#AAA1FF");
                return;
            }
            if (!_mangaDexListsLoaded)
            {
                SyncMangaDexLists();
                return;
            }
            if (_mangaDexLists.Count == 0)
            {
                ToggleLocalFavorite(item);
                _status.Text = "No MangaDex MDLists found; this favourite is stored locally.";
                _status.Foreground = Brush("#9999A8");
                return;
            }

            bool adding = !IsFavorite(item.Id);
            List<string> listIds;
            if (adding)
            {
                listIds = _mangaDexLists
                    .Where(list => !list.MangaIds.Contains(item.Id))
                    .Select(list => list.Id)
                    .ToList();
            }
            else
            {
                HashSet<string> existingLists;
                listIds = _mangaDexListMembership.TryGetValue(item.Id, out existingLists)
                    ? existingLists.ToList()
                    : new List<string>();
            }

            _isUpdatingFavoriteLists = true;
            _status.Text = adding
                ? "Adding this manga to all MangaDex MDLists..."
                : "Removing this manga from its MangaDex MDLists...";
            _status.Foreground = Brush("#AAA1FF");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    foreach (string listId in listIds)
                    {
                        string path = "/manga/" + Uri.EscapeDataString(item.Id)
                            + "/list/" + Uri.EscapeDataString(listId);
                        AuthenticatedMangaDexRequest(adding ? "POST" : "DELETE", path, null);
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (string listId in listIds)
                        {
                            MangaDexList list = _mangaDexLists.FirstOrDefault(
                                value => String.Equals(value.Id, listId, StringComparison.OrdinalIgnoreCase));
                            if (list == null) continue;
                            if (adding) list.MangaIds.Add(item.Id);
                            else list.MangaIds.Remove(item.Id);
                        }
                        if (adding)
                        {
                            HashSet<string> itemLists;
                            if (!_mangaDexListMembership.TryGetValue(item.Id, out itemLists))
                            {
                                itemLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                _mangaDexListMembership.Add(item.Id, itemLists);
                            }
                            foreach (string listId in _mangaDexLists.Select(list => list.Id))
                                itemLists.Add(listId);
                            item.ListNames = String.Join(", ", _mangaDexLists
                                .Select(list => list.Name)
                                .Where(name => !String.IsNullOrWhiteSpace(name))
                                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));
                            if (!_favorites.Any(value => value.Id == item.Id))
                                _favorites.Add(item);
                        }
                        else
                        {
                            _mangaDexListMembership.Remove(item.Id);
                            _favorites.RemoveAll(value => value.Id == item.Id);
                        }
                        _isUpdatingFavoriteLists = false;
                        SaveFavorites();
                        SetPage(_page);
                        _status.Text = adding
                            ? "Manga added to all MangaDex MDLists."
                            : "Manga removed from all MangaDex MDLists that contained it.";
                        _status.Foreground = Brush("#C5F36A");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _isUpdatingFavoriteLists = false;
                        _mangaDexListsLoaded = false;
                        _status.Text = "Could not update all MangaDex MDLists; sync them again to reconcile: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
            });
        }

        private void ToggleLocalFavorite(MangaItem item)
        {
            MangaItem existing = _favorites.FirstOrDefault(value => value.Id == item.Id);
            if (existing == null) _favorites.Add(item);
            else _favorites.Remove(existing);
            SaveFavorites();
            SetPage(_page);
        }

        private void ToggleSavedChapter(ChapterItem chapter)
        {
            ChapterItem existing = _savedChapters.FirstOrDefault(value => value.Id == chapter.Id);
            if (existing == null)
            {
                _savedChapters.Add(chapter);
                EnsureMangaMetadata(chapter);
            }
            else _savedChapters.Remove(existing);
            SaveSavedChapters();
            SetPage(_page);
            if (HasMangaDexAccount) SyncReadProgress();
        }

        private void SaveChapterAutomatically(ChapterItem chapter)
        {
            if (chapter == null || String.IsNullOrWhiteSpace(chapter.Id))
                return;
            if (String.IsNullOrWhiteSpace(chapter.Source)) chapter.Source = "MangaDex";
            ChapterItem existing = _savedChapters.FirstOrDefault(
                item => String.Equals(item.Id, chapter.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (String.IsNullOrWhiteSpace(existing.MangaTitle)
                    || existing.MangaTitle == "Saved manga"
                    || existing.MangaTitle == "MangaDex chapter")
                    existing.MangaTitle = chapter.MangaTitle;
                if (!String.IsNullOrWhiteSpace(chapter.Number)) existing.Number = chapter.Number;
                if (!String.IsNullOrWhiteSpace(chapter.Title)) existing.Title = chapter.Title;
                if (!String.IsNullOrWhiteSpace(chapter.Volume)) existing.Volume = chapter.Volume;
                if (String.IsNullOrWhiteSpace(existing.CoverUrl)) existing.CoverUrl = chapter.CoverUrl;
                if (!String.IsNullOrWhiteSpace(chapter.MaxChapter)) existing.MaxChapter = chapter.MaxChapter;
                if (String.IsNullOrWhiteSpace(existing.Source)) existing.Source = chapter.Source;
                existing.PageNumber = chapter.PageNumber;
                existing.MangaMetadataLoaded = existing.MangaMetadataLoaded || chapter.MangaMetadataLoaded;
                _savedChapters.Remove(existing);
                _savedChapters.Add(existing);
                SaveSavedChapters();
                EnsureMangaMetadata(existing);
                return;
            }
            _savedChapters.Add(chapter);
            SaveSavedChapters();
            EnsureMangaMetadata(chapter);
            if (_page == "Saved chapters") RenderCurrentPage();
            string label = String.IsNullOrWhiteSpace(chapter.Number) ? "chapter" : "chapter " + chapter.Number;
            _status.Text = label + " added to Saved chapters.";
            _status.Foreground = Brush("#C5F36A");
        }

        private void DeleteSavedChapter(ChapterItem chapter)
        {
            _savedChapters.RemoveAll(item => String.Equals(
                item.MangaId, chapter.MangaId, StringComparison.OrdinalIgnoreCase));
            SaveSavedChapters();
            RenderCurrentPage();
        }

        private void EnsureMangaMetadata(ChapterItem chapter)
        {
            if (chapter.MangaMetadataLoaded || String.IsNullOrWhiteSpace(chapter.MangaId))
                return;
            lock (_mangaMetadataLock)
            {
                if (!_mangaMetadataRequests.Add(chapter.MangaId))
                    return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    MangaItem metadata = FetchMangaMetadata(chapter.MangaId);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        foreach (ChapterItem item in _savedChapters.Concat(_chapters)
                            .Where(value => String.Equals(value.MangaId, metadata.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (String.IsNullOrWhiteSpace(item.MangaTitle)
                                || item.MangaTitle == "Saved manga"
                                || item.MangaTitle == "MangaDex chapter")
                                item.MangaTitle = metadata.Title;
                            if (String.IsNullOrWhiteSpace(item.CoverUrl))
                                item.CoverUrl = metadata.CoverUrl;
                            if (String.IsNullOrWhiteSpace(item.MaxChapter))
                                item.MaxChapter = metadata.LastChapter;
                            if (String.IsNullOrWhiteSpace(item.Source))
                                item.Source = "MangaDex";
                            item.MangaMetadataLoaded = true;
                        }
                        SaveSavedChapters();
                        if (_page == "Saved chapters") RenderCurrentPage();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        _status.Text = "Could not load MangaDex cover or progress details: " + ex.Message;
                        _status.Foreground = Brush("#FF9393");
                    }));
                }
                finally
                {
                    lock (_mangaMetadataLock) _mangaMetadataRequests.Remove(chapter.MangaId);
                }
            });
        }

        private bool IsFavorite(string id)
        {
            return _favorites.Any(item => item.Id == id);
        }

        private bool IsChapterSaved(string id)
        {
            return _savedChapters.Any(item => item.Id == id);
        }

        private void LoadFavorites()
        {
            try
            {
                if (!File.Exists(FavoritesPath)) return;
                ArrayList values = AsArray(Json.DeserializeObject(File.ReadAllText(FavoritesPath, Encoding.UTF8)));
                foreach (object value in values)
                {
                    Dictionary<string, object> row = AsDictionary(value);
                    string id = StringValue(Value(row, "Id"));
                    string title = StringValue(Value(row, "Title"));
                    if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(title)) continue;
                    _favorites.Add(new MangaItem
                    {
                        Id = id,
                        Title = title,
                        Status = StringValue(Value(row, "Status")),
                        CoverUrl = StringValue(Value(row, "CoverUrl")),
                        LastChapter = StringValue(Value(row, "LastChapter")),
                        ListNames = StringValue(Value(row, "ListNames")),
                        SourceName = StringValue(Value(row, "SourceName")),
                        SourceUrl = StringValue(Value(row, "SourceUrl")),
                        MetadataLoaded = BooleanValue(Value(row, "MetadataLoaded"))
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved favourites could not be read:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveFavorites()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                string json = Json.Serialize(_favorites.Select(item => new
                {
                    item.Id,
                    item.Title,
                    item.Status,
                    item.CoverUrl,
                    item.LastChapter,
                    item.ListNames,
                    item.SourceName,
                    item.SourceUrl,
                    item.MetadataLoaded
                }).ToArray());
                File.WriteAllText(FavoritesPath, json, new UTF8Encoding(false));
                if (_page == "Favourites") _subtitle.Text = FavoritesSummary();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Favourites could not be saved:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSavedChapters()
        {
            try
            {
                if (!File.Exists(SavedChaptersPath)) return;
                ArrayList values = AsArray(Json.DeserializeObject(File.ReadAllText(SavedChaptersPath, Encoding.UTF8)));
                foreach (object value in values)
                {
                    Dictionary<string, object> row = AsDictionary(value);
                    string id = StringValue(Value(row, "Id"));
                    string mangaId = StringValue(Value(row, "MangaId"));
                    string mangaTitle = StringValue(Value(row, "MangaTitle"));
                    if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(mangaId)) continue;
                    _savedChapters.Add(new ChapterItem
                    {
                        Id = id,
                        MangaId = mangaId,
                        MangaTitle = String.IsNullOrWhiteSpace(mangaTitle) ? "Saved manga" : mangaTitle,
                        Number = StringValue(Value(row, "Number")),
                        Title = StringValue(Value(row, "Title")),
                        Volume = StringValue(Value(row, "Volume")),
                        CoverUrl = StringValue(Value(row, "CoverUrl")),
                        Source = StringValue(Value(row, "Source")),
                        MaxChapter = StringValue(Value(row, "MaxChapter")),
                        MangaMetadataLoaded = BooleanValue(Value(row, "MangaMetadataLoaded")),
                        IsRead = BooleanValue(Value(row, "IsRead")),
                        PageNumber = IntegerValue(Value(row, "PageNumber"))
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved chapters could not be read:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveSavedChapters()
        {
            _savedChaptersSaveTimer.Stop();
            _savedChaptersSaveTimer.Start();
            if (_page == "Saved chapters")
                _subtitle.Text = SavedSummary();
        }

        private void WriteSavedChapters()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                string json = Json.Serialize(_savedChapters.Select(item => new
                {
                    item.Id,
                    item.MangaId,
                    item.MangaTitle,
                    item.Number,
                    item.Title,
                    item.Volume,
                    item.CoverUrl,
                    item.Source,
                    item.MaxChapter,
                    item.MangaMetadataLoaded,
                    item.IsRead,
                    item.PageNumber
                }).ToArray());
                File.WriteAllText(SavedChaptersPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved chapters could not be written:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadSources()
        {
            _sources.Add(new SourceItem
            {
                Name = "MangaDex",
                Url = "https://mangadex.org/",
                Note = "Catalogue and cover art via the public API.",
                Color = "#F18F76"
            });
            _sources.Add(new SourceItem
            {
                Name = "ThunderScans",
                Url = "https://thunderscans.com/",
                Note = "Website shortcut. No catalogue is scraped.",
                Color = "#AAA1FF"
            });
            _sources.Add(new SourceItem
            {
                Name = "MangaFire",
                Url = "https://mangafire.to/home",
                Note = "Website shortcut. No catalogue is scraped.",
                Color = "#C5F36A"
            });
            try
            {
                if (!File.Exists(SourcesPath)) return;
                ArrayList values = AsArray(Json.DeserializeObject(File.ReadAllText(SourcesPath, Encoding.UTF8)));
                foreach (object value in values)
                {
                    Dictionary<string, object> row = AsDictionary(value);
                    string name = StringValue(Value(row, "Name"));
                    string url = StringValue(Value(row, "Url"));
                    Uri parsed;
                    if (String.IsNullOrWhiteSpace(name)
                        || !Uri.TryCreate(url, UriKind.Absolute, out parsed)
                        || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
                        || _sources.Any(source => String.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    _sources.Add(new SourceItem
                    {
                        Name = name,
                        Url = parsed.AbsoluteUri,
                        Note = "Custom source shortcut",
                        Color = "#AAA1FF"
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Saved sources could not be read:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveSources()
        {
            try
            {
                Directory.CreateDirectory(RootPath);
                string json = Json.Serialize(_sources
                    .Where(source => source.Note == "Custom source shortcut")
                    .Select(source => new { source.Name, source.Url }).ToArray());
                File.WriteAllText(SourcesPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sources could not be saved:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void OpenUrl(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                MessageBox.Show("This source link is not a valid web URL.", "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the source link:\n" + ex.Message, "MangaXcat", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string SafeFileName(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string RunCurl(string url, string outputPath)
        {
            string curlPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            if (!File.Exists(curlPath))
                throw new InvalidOperationException("The Windows curl.exe utility is required to contact MangaDex.");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = "--fail --silent --show-error --max-time 20 --user-agent \"MangaXcatDesktop/1.0\" "
                    + (String.IsNullOrWhiteSpace(outputPath) ? "--header \"Accept: application/json\" " : "--output \"" + outputPath + "\" ")
                    + "--url \"" + url + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = String.IsNullOrWhiteSpace(outputPath),
                RedirectStandardError = true
            };
            if (start.RedirectStandardOutput) start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(start))
            {
                string result = start.RedirectStandardOutput ? process.StandardOutput.ReadToEnd() : String.Empty;
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(String.IsNullOrWhiteSpace(error) ? "The MangaDex request failed." : error.Trim());
                return result;
            }
        }

        private static object Value(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static ArrayList AsArray(object value)
        {
            ArrayList list = value as ArrayList;
            if (list != null) return list;
            object[] items = value as object[];
            return items == null ? new ArrayList() : new ArrayList(items);
        }

        private static string StringValue(object value)
        {
            return value == null ? null : Convert.ToString(value);
        }

        private static bool BooleanValue(object value)
        {
            bool result;
            return value is bool
                ? (bool)value
                : Boolean.TryParse(StringValue(value), out result) && result;
        }

        private static int IntegerValue(object value)
        {
            int result;
            return Int32.TryParse(
                StringValue(value),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result)
                ? result
                : 0;
        }

        private static TextBlock Label(string text, double size, string color, FontWeight weight)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = Brush(color),
                FontWeight = weight
            };
        }

        private static Brush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private static void StyleButton(Button button, string background, string foreground)
        {
            button.Height = 36;
            button.Padding = new Thickness(13, 0, 13, 0);
            button.Background = Brush(background);
            button.Foreground = Brush(foreground);
            button.BorderThickness = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.FontSize = 11;
            button.FontWeight = FontWeights.SemiBold;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MangaWindow());
        }
    }
}
