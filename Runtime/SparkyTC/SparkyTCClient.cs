// Only in the Editor (the window and tests): the headset never talks to SparkyTC.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace XRSessionCapture.SparkyTC
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

        [Serializable]
        public class TestRunCase
        {
            public long testCaseNumber;
            public string title;
            /// <summary>PASS, FAIL, BLOCKED, SKIP or NOT_RUN.</summary>
            public string status;
            public string webPath;
        }

        [Serializable]
        public class TestRun
        {
            public string testRunID;
            public long testRunNumber;
            public string title;
            public string status;
            public Build build;
            public bool automated;
            public TestRunCase[] testCases;
            public string webPath;
            public string sourceRef;
            /// <summary>False when a run with the same sourceRef already existed (published before).</summary>
            public bool created;
        }

        [Serializable]
        public class RecordedResult
        {
            public string resultID;
            public long testRunNumber;
            public long testCaseNumber;
            public string status;
            public string testRunStatus;
            public string webPath;
        }

        /// <summary>A file to attach to a result.</summary>
        public struct Evidence
        {
            public string fileName;
            public string contentType;
            public byte[] data;
        }

        [Serializable] class ProjectResponse { public Project data; }
        [Serializable] class TestRunResponse { public TestRun data; }
        [Serializable] class RecordedResultResponse { public RecordedResult data; }
        [Serializable] class TestCasesResponse { public TestCase[] data; }
        [Serializable] class ErrorResponse { public string error; public string message; }

        // timeouts are per request (uploads take longer), so the shared client has none of its own
        static readonly HttpClient s_Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        readonly string m_ApiUrl;
        readonly string m_Token;

        public SparkyTCClient(string apiUrl, string token)
        {
            m_ApiUrl = (apiUrl ?? string.Empty).Trim().TrimEnd('/');
            m_Token = (token ?? string.Empty).Trim();
        }

        
        public async Task<Project> GetProjectAsync()
        {
            var json = await SendAsync(HttpMethod.Get, "/project").ConfigureAwait(false);
            return JsonUtility.FromJson<ProjectResponse>(json).data;
        }

        /// <summary>Test cases whose number or title match (all when the query is empty), up to <paramref name="limit"/>.</summary>
        public async Task<TestCase[]> SearchTestCasesAsync(string query, int limit = 50)
        {
            var path = "/test-cases?limit=" + limit + (string.IsNullOrWhiteSpace(query) ? string.Empty : "&query=" + Uri.EscapeDataString(query.Trim()));
            var json = await SendAsync(HttpMethod.Get, path).ConfigureAwait(false);
            return JsonUtility.FromJson<TestCasesResponse>(json).data ?? Array.Empty<TestCase>();
        }

        /// <summary>Creates an automated test run, or returns the one created before with the same sourceRef.</summary>
        public async Task<TestRun> CreateTestRunAsync(string title, string build, IEnumerable<long> testCaseNumbers, string description, string sourceRef)
        {
            var body = new Json()
                .Add("title", title)
                .Add("build", build)
                .Add("testCaseNumbers", testCaseNumbers)
                .Add("description", description)
                .Add("sourceRef", sourceRef);
            var json = await SendAsync(HttpMethod.Post, "/test-runs", JsonContent(body)).ConfigureAwait(false);
            return JsonUtility.FromJson<TestRunResponse>(json).data;
        }

        public async Task<RecordedResult> RecordResultAsync(long testRunNumber, long testCaseNumber, string status, string notes, long? durationSeconds, Json report)
        {
            var body = new Json()
                .Add("testCaseNumber", testCaseNumber)
                .Add("status", status)
                .Add("notes", notes)
                .Add("durationSeconds", durationSeconds)
                .Add("report", report);
            var json = await SendAsync(HttpMethod.Post, $"/test-runs/{testRunNumber}/results", JsonContent(body)).ConfigureAwait(false);
            return JsonUtility.FromJson<RecordedResultResponse>(json).data;
        }

        public async Task UploadEvidenceAsync(long testRunNumber, string resultID, IReadOnlyList<Evidence> files)
        {
            if (files.Count == 0)
                return;
            using (var content = new MultipartFormDataContent())
            {
                foreach (var file in files)
                {
                    var part = new ByteArrayContent(file.data);
                    part.Headers.ContentType = new MediaTypeHeaderValue(file.contentType);
                    content.Add(part, "files", file.fileName);
                }
                await SendAsync(HttpMethod.Post, $"/test-runs/{testRunNumber}/results/{resultID}/attachments", content, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
        }

        static HttpContent JsonContent(Json body) => new StringContent(body.ToString(), Encoding.UTF8, "application/json");

        protected async Task<string> SendAsync(HttpMethod method, string path, HttpContent content = null, TimeSpan? timeout = null)
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
                var limit = timeout ?? TimeSpan.FromSeconds(15);
                using (var cancel = new CancellationTokenSource(limit))
                {
                    try
                    {
                        response = await s_Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new SparkyTCException($"SparkyTC at {m_ApiUrl} didn't answer in {limit.TotalSeconds:0} seconds.");
                    }
                    catch (HttpRequestException e)
                    {
                        throw new SparkyTCException($"Can't reach SparkyTC at {m_ApiUrl}: {e.GetBaseException().Message}. Check the URL and that this PC can reach the server.");
                    }
                }

                using (response)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
#endif
