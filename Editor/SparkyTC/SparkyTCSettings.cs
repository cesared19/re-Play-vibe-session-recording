using UnityEditor;

namespace XRSessionCapture.Editor.SparkyTC
{
    /// <summary>
    /// Where SparkyTC is and the project token, kept in EditorPrefs: on this PC only, never in the project or in a
    /// build. The headset never talks to SparkyTC.
    /// </summary>
    public static class SparkyTCSettings
    {
        const string UrlKey = "XRSessionCapture.SparkyTC.Url";
        const string ApiUrlKey = "XRSessionCapture.SparkyTC.ApiUrl";
        const string TokenKey = "XRSessionCapture.SparkyTC.Token";

        /// <summary>The SparkyTC web app, e.g. http://192.168.1.20:3000. Links to test cases open here.</summary>
        public static string Url
        {
            get => EditorPrefs.GetString(UrlKey, string.Empty);
            set => EditorPrefs.SetString(UrlKey, (value ?? string.Empty).Trim());
        }

        /// <summary>Where the integration API answers, if not at <see cref="Url"/> (e.g. an SSH tunnel to the backend).</summary>
        public static string ApiUrl
        {
            get => EditorPrefs.GetString(ApiUrlKey, string.Empty);
            set => EditorPrefs.SetString(ApiUrlKey, (value ?? string.Empty).Trim());
        }

        /// <summary>A project API token (stc_...). Read is enough to link captures; publishing results needs Read and write.</summary>
        public static string Token
        {
            get => EditorPrefs.GetString(TokenKey, string.Empty);
            set => EditorPrefs.SetString(TokenKey, (value ?? string.Empty).Trim());
        }

        public static bool IsConfigured => !string.IsNullOrEmpty(EffectiveApiUrl) && !string.IsNullOrEmpty(Token);

        public static string EffectiveApiUrl => (string.IsNullOrEmpty(ApiUrl) ? Url : ApiUrl).TrimEnd('/');

        /// <summary>A page of the web app, from a webPath the API returned.</summary>
        public static string WebUrl(string webPath)
        {
            var url = Url.TrimEnd('/');
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(webPath) ? null : url + webPath;
        }
    }
}
