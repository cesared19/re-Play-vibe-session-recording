using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using UnityEngine;

namespace XRSessionCapture.Editor.SparkyTC
{
    /// <summary>A failed call to SparkyTC, with a message for the person using the window.</summary>
    public class SparkyTCException : Exception
    {
        public int StatusCode { get; }

        public SparkyTCException(string message, int statusCode = 0) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>The parts of SparkyTC's integration API (/api/integration/v1) the Editor uses. Runs on the PC only.</summary>
    public class SparkyTCClient
    {
        [Serializable]
        public class Build
        {
            public string buildID;
            public string buildNumber;
        }

        [Serializable]
        public class Project
        {
            public string projectShortCode;
            public string title;
            public Build currentBuild;
            public Build[] builds;
            public string[] permissions;

            public bool CanWrite => permissions != null && Array.IndexOf(permissions, "write") >= 0;
        }

        [Serializable]
        public class TestCase
        {
            public string testCaseID;
            public long testCaseNumber;
            public string title;
            public string sectionPath;
            public string[] labels;
            public string webPath;
        }

        [Serializable] class ProjectResponse { public Project data; }
        [Serializable] class TestCasesResponse { public TestCase[] data; }
        [Serializable] class ErrorResponse { public string error; public string message; }

        static readonly HttpClient s_Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        readonly string m_ApiUrl;
        readonly string m_Token;

        public SparkyTCClient(string apiUrl, string token)
        {
            m_ApiUrl = (apiUrl ?? string.Empty).Trim().TrimEnd('/');
            m_Token = (token ?? string.Empty).Trim();
        }

        public static SparkyTCClient FromSettings() => new SparkyTCClient(SparkyTCSettings.EffectiveApiUrl, SparkyTCSettings.Token);

        public async Task<Project> GetProjectAsync()
        {
            var json = await SendAsync(HttpMethod.Get, "/project");
            return JsonUtility.FromJson<ProjectResponse>(json).data;
        }

        /// <summary>Test cases whose number or title match (all when the query is empty), up to <paramref name="limit"/>.</summary>
        public async Task<TestCase[]> SearchTestCasesAsync(string query, int limit = 50)
        {
            var path = "/test-cases?limit=" + limit + (string.IsNullOrWhiteSpace(query) ? string.Empty : "&query=" + Uri.EscapeDataString(query.Trim()));
            var json = await SendAsync(HttpMethod.Get, path);
            return JsonUtility.FromJson<TestCasesResponse>(json).data ?? Array.Empty<TestCase>();
        }

        protected async Task<string> SendAsync(HttpMethod method, string path, HttpContent content = null)
        {
            if (string.IsNullOrEmpty(m_ApiUrl) || !Uri.TryCreate(m_ApiUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
                throw new SparkyTCException("Set the SparkyTC URL first, e.g. http://192.168.1.20:3000.");
            if (string.IsNullOrEmpty(m_Token))
                throw new SparkyTCException("Paste a project API token (an admin creates it in SparkyTC: the project's Settings → API tokens).");

            using (var request = new HttpRequestMessage(method, m_ApiUrl + "/api/integration/v1" + path) { Content = content })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", m_Token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                HttpResponseMessage response;
                try
                {
                    response = await s_Http.SendAsync(request);
                }
                catch (TaskCanceledException)
                {
                    throw new SparkyTCException($"SparkyTC at {m_ApiUrl} didn't answer in {s_Http.Timeout.TotalSeconds:0} seconds.");
                }
                catch (HttpRequestException e)
                {
                    throw new SparkyTCException($"Can't reach SparkyTC at {m_ApiUrl}: {e.GetBaseException().Message}. Check the URL and that this PC can reach the server.");
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        if (!LooksLikeJson(body))
                            throw new SparkyTCException($"{m_ApiUrl} answered, but not like SparkyTC's integration API. Check the URL (see the step-by-step guide).", (int)response.StatusCode);
                        return body;
                    }
                    throw new SparkyTCException(ErrorMessage((int)response.StatusCode, body), (int)response.StatusCode);
                }
            }
        }

        static bool LooksLikeJson(string body)
        {
            var trimmed = body?.TrimStart();
            return !string.IsNullOrEmpty(trimmed) && trimmed[0] == '{';
        }

        static string ErrorMessage(int status, string body)
        {
            string message = null;
            if (LooksLikeJson(body))
            {
                try
                {
                    message = JsonUtility.FromJson<ErrorResponse>(body)?.message;
                }
                catch (ArgumentException)
                {
                }
            }
            switch (status)
            {
                case 401: return "SparkyTC didn't accept the token: it's wrong or was revoked. Ask an admin for a new one.";
                case 403: return message ?? "This token can only read. Publishing needs a Read and write token.";
                case 404 when message == null: return "SparkyTC's integration API isn't at this URL (404). Check the URL.";
                default: return string.IsNullOrEmpty(message) ? $"SparkyTC answered {status}." : message;
            }
        }
    }
}
