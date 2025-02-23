using Gtk;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.Ui.Common.Configuration;
using Ryujinx.Ui.Common.Models.Amiibo;
using Ryujinx.Ui.Widgets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Ryujinx.Ui.Windows
{
    public partial class AmiiboWindow : Window
    {
        private const string DefaultJson = "{ \"amiibo\": [] }";

        public string AmiiboId { get; private set; }

        public int DeviceId { get; set; }
        public string TitleId { get; set; }
        public string LastScannedAmiiboId { get; set; }
        public bool LastScannedAmiiboShowAll { get; set; }

        public ResponseType Response { get; private set; }

        public bool UseRandomUuid
        {
            get
            {
                return _randomUuidCheckBox.Active;
            }
        }

        private readonly HttpClient _httpClient;
        private readonly string _amiiboJsonPath;

        private readonly byte[] _amiiboLogoBytes;

        private List<AmiiboApi> _amiiboList;

        private static readonly AmiiboJsonSerializerContext _serializerContext = new(JsonHelper.GetDefaultSerializerOptions());

        public AmiiboWindow() : base($"Ryujinx {Program.Version} - Amiibo")
        {
            Icon = new Gdk.Pixbuf(Assembly.GetAssembly(typeof(ConfigurationState)), "Ryujinx.Ui.Common.Resources.Logo_Ryujinx.png");

            InitializeComponent();

            _httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(30),
            };

            Directory.CreateDirectory(System.IO.Path.Join(AppDataManager.BaseDirPath, "system", "amiibo"));

            _amiiboJsonPath = System.IO.Path.Join(AppDataManager.BaseDirPath, "system", "amiibo", "Amiibo.json");
            _amiiboList = new List<AmiiboApi>();

            _amiiboLogoBytes = EmbeddedResources.Read("Ryujinx.Ui.Common/Resources/Logo_Amiibo.png");
            _amiiboImage.Pixbuf = new Gdk.Pixbuf(_amiiboLogoBytes);

            _scanButton.Sensitive = false;
            _randomUuidCheckBox.Sensitive = false;

            _ = LoadContentAsync();
        }

        private bool TryGetAmiiboJson(string json, out AmiiboJson amiiboJson)
        {
            try
            {
                amiiboJson = JsonHelper.Deserialize<AmiiboJson>(json, _serializerContext.AmiiboJson);

                return true;
            }
            catch
            {
                amiiboJson = JsonHelper.Deserialize<AmiiboJson>(DefaultJson, _serializerContext.AmiiboJson);

                return false;
            }
        }

        private async Task<AmiiboJson> GetMostRecentAmiiboListOrDefaultJson()
        {
            bool localIsValid = false;
            bool remoteIsValid = false;
            AmiiboJson amiiboJson = JsonHelper.Deserialize<AmiiboJson>(DefaultJson, _serializerContext.AmiiboJson);

            try
            {
                localIsValid = TryGetAmiiboJson(File.ReadAllText(_amiiboJsonPath), out amiiboJson);

                if (!localIsValid || await NeedsUpdate(amiiboJson.LastUpdated))
                {
                    remoteIsValid = TryGetAmiiboJson(await DownloadAmiiboJson(), out amiiboJson);
                }
            }
            catch
            {
                if (!(localIsValid || remoteIsValid))
                {
                    // Neither local or remote files are valid JSON, close window.
                    ShowInfoDialog();
                    Close();
                }
                else if (!remoteIsValid)
                {
                    // Only the local file is valid, the local one should be used
                    // but the user should be warned.
                    ShowInfoDialog();
                }
            }

            return amiiboJson;
        }

        private async Task LoadContentAsync()
        {
            AmiiboJson amiiboJson = await GetMostRecentAmiiboListOrDefaultJson();

            _amiiboList = amiiboJson.Amiibo.OrderBy(amiibo => amiibo.AmiiboSeries).ToList();

            if (LastScannedAmiiboShowAll)
            {
                _showAllCheckBox.Click();
            }

            ParseAmiiboData();

            _showAllCheckBox.Clicked += ShowAllCheckBox_Clicked;
        }

        private void ParseAmiiboData()
        {
            List<string> comboxItemList = new();

            for (int i = 0; i < _amiiboList.Count; i++)
            {
                if (!comboxItemList.Contains(_amiiboList[i].AmiiboSeries))
                {
                    if (!_showAllCheckBox.Active)
                    {
                        foreach (var game in _amiiboList[i].GamesSwitch)
                        {
                            if (game != null)
                            {
                                if (game.GameId.Contains(TitleId))
                                {
                                    comboxItemList.Add(_amiiboList[i].AmiiboSeries);
                                    _amiiboSeriesComboBox.Append(_amiiboList[i].AmiiboSeries, _amiiboList[i].AmiiboSeries);

                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        comboxItemList.Add(_amiiboList[i].AmiiboSeries);
                        _amiiboSeriesComboBox.Append(_amiiboList[i].AmiiboSeries, _amiiboList[i].AmiiboSeries);
                    }
                }
            }

            _amiiboSeriesComboBox.Changed += SeriesComboBox_Changed;
            _amiiboCharsComboBox.Changed += CharacterComboBox_Changed;

            if (LastScannedAmiiboId != "")
            {
                SelectLastScannedAmiibo();
            }
            else
            {
                _amiiboSeriesComboBox.Active = 0;
            }
        }

        private void SelectLastScannedAmiibo()
        {
            bool isSet = _amiiboSeriesComboBox.SetActiveId(_amiiboList.Find(amiibo => amiibo.Head + amiibo.Tail == LastScannedAmiiboId).AmiiboSeries);
            isSet = _amiiboCharsComboBox.SetActiveId(LastScannedAmiiboId);

            if (isSet == false)
            {
                _amiiboSeriesComboBox.Active = 0;
            }
        }

        private async Task<bool> NeedsUpdate(DateTime oldLastModified)
        {
            HttpResponseMessage response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://amiibo.ryujinx.org/"));

            if (response.IsSuccessStatusCode)
            {
                return response.Content.Headers.LastModified != oldLastModified;
            }

            return false;
        }

        private async Task<string> DownloadAmiiboJson()
        {
            HttpResponseMessage response = await _httpClient.GetAsync("https://amiibo.ryujinx.org/");

            if (response.IsSuccessStatusCode)
            {
                string amiiboJsonString = await response.Content.ReadAsStringAsync();

                using (FileStream dlcJsonStream = File.Create(_amiiboJsonPath, 4096, FileOptions.WriteThrough))
                {
                    dlcJsonStream.Write(Encoding.UTF8.GetBytes(amiiboJsonString));
                }

                return amiiboJsonString;
            }
            else
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to download amiibo data. Response status code: {response.StatusCode}");

                GtkDialog.CreateInfoDialog($"Amiibo API", "An error occured while fetching information from the API.");

                Close();
            }

            return DefaultJson;
        }

        private async Task UpdateAmiiboPreview(string imageUrl)
        {
            HttpResponseMessage response = await _httpClient.GetAsync(imageUrl);

            if (response.IsSuccessStatusCode)
            {
                byte[] amiiboPreviewBytes = await response.Content.ReadAsByteArrayAsync();
                Gdk.Pixbuf amiiboPreview = new(amiiboPreviewBytes);

                float ratio = Math.Min((float)_amiiboImage.AllocatedWidth / amiiboPreview.Width,
                                       (float)_amiiboImage.AllocatedHeight / amiiboPreview.Height);

                int resizeHeight = (int)(amiiboPreview.Height * ratio);
                int resizeWidth = (int)(amiiboPreview.Width * ratio);

                _amiiboImage.Pixbuf = amiiboPreview.ScaleSimple(resizeWidth, resizeHeight, Gdk.InterpType.Bilinear);
            }
            else
            {
                Logger.Error?.Print(LogClass.Application, $"Failed to get amiibo preview. Response status code: {response.StatusCode}");
            }
        }

        private static void ShowInfoDialog()
        {
            GtkDialog.CreateInfoDialog($"Amiibo API", "Unable to connect to Amiibo API server. The service may be down or you may need to verify your internet connection is online.");
        }

        //
        // Events
        //
        private void SeriesComboBox_Changed(object sender, EventArgs args)
        {
            _amiiboCharsComboBox.Changed -= CharacterComboBox_Changed;

            _amiiboCharsComboBox.RemoveAll();

            List<AmiiboApi> amiiboSortedList = _amiiboList.Where(amiibo => amiibo.AmiiboSeries == _amiiboSeriesComboBox.ActiveId).OrderBy(amiibo => amiibo.Name).ToList();

            List<string> comboxItemList = new();

            for (int i = 0; i < amiiboSortedList.Count; i++)
            {
                if (!comboxItemList.Contains(amiiboSortedList[i].Head + amiiboSortedList[i].Tail))
                {
                    if (!_showAllCheckBox.Active)
                    {
                        foreach (var game in amiiboSortedList[i].GamesSwitch)
                        {
                            if (game != null)
                            {
                                if (game.GameId.Contains(TitleId))
                                {
                                    comboxItemList.Add(amiiboSortedList[i].Head + amiiboSortedList[i].Tail);
                                    _amiiboCharsComboBox.Append(amiiboSortedList[i].Head + amiiboSortedList[i].Tail, amiiboSortedList[i].Name);

                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        comboxItemList.Add(amiiboSortedList[i].Head + amiiboSortedList[i].Tail);
                        _amiiboCharsComboBox.Append(amiiboSortedList[i].Head + amiiboSortedList[i].Tail, amiiboSortedList[i].Name);
                    }
                }
            }

            _amiiboCharsComboBox.Changed += CharacterComboBox_Changed;

            _amiiboCharsComboBox.Active = 0;

            _scanButton.Sensitive = true;
            _randomUuidCheckBox.Sensitive = true;
        }

        private void CharacterComboBox_Changed(object sender, EventArgs args)
        {
            AmiiboId = _amiiboCharsComboBox.ActiveId;

            _amiiboImage.Pixbuf = new Gdk.Pixbuf(_amiiboLogoBytes);

            string imageUrl = _amiiboList.Find(amiibo => amiibo.Head + amiibo.Tail == _amiiboCharsComboBox.ActiveId).Image;

            var usageStringBuilder = new StringBuilder();

            for (int i = 0; i < _amiiboList.Count; i++)
            {
                if (_amiiboList[i].Head + _amiiboList[i].Tail == _amiiboCharsComboBox.ActiveId)
                {
                    bool writable = false;

                    foreach (var item in _amiiboList[i].GamesSwitch)
                    {
                        if (item.GameId.Contains(TitleId))
                        {
                            foreach (AmiiboApiUsage usageItem in item.AmiiboUsage)
                            {
                                usageStringBuilder.Append(Environment.NewLine);
                                usageStringBuilder.Append($"- {usageItem.Usage.Replace("/", Environment.NewLine + "-")}");

                                writable = usageItem.Write;
                            }
                        }
                    }

                    if (usageStringBuilder.Length == 0)
                    {
                        usageStringBuilder.Append("Unknown.");
                    }

                    _gameUsageLabel.Text = $"Usage{(writable ? " (Writable)" : "")} : {usageStringBuilder}";
                }
            }

            _ = UpdateAmiiboPreview(imageUrl);
        }

        private void ShowAllCheckBox_Clicked(object sender, EventArgs e)
        {
            _amiiboImage.Pixbuf = new Gdk.Pixbuf(_amiiboLogoBytes);

            _amiiboSeriesComboBox.Changed -= SeriesComboBox_Changed;
            _amiiboCharsComboBox.Changed -= CharacterComboBox_Changed;

            _amiiboSeriesComboBox.RemoveAll();
            _amiiboCharsComboBox.RemoveAll();

            _scanButton.Sensitive = false;
            _randomUuidCheckBox.Sensitive = false;

            new Task(() => ParseAmiiboData()).Start();
        }

        private void ScanButton_Pressed(object sender, EventArgs args)
        {
            LastScannedAmiiboShowAll = _showAllCheckBox.Active;

            Response = ResponseType.Ok;

            Close();
        }

        private void CancelButton_Pressed(object sender, EventArgs args)
        {
            AmiiboId = "";
            LastScannedAmiiboId = "";
            LastScannedAmiiboShowAll = false;

            Response = ResponseType.Cancel;

            Close();
        }

        protected override void Dispose(bool disposing)
        {
            _httpClient.Dispose();

            base.Dispose(disposing);
        }
    }
}
