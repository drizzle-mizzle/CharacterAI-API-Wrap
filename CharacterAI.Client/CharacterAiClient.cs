﻿using System.Diagnostics;
using System.Dynamic;
using Newtonsoft.Json;
using static SharedUtils.Common;
using PuppeteerLib.Models;
using static PuppeteerLib.RequestsUtils;
using PuppeteerSharp;
using CharacterAI.Models.Result;
using CharacterAI.Models.Result;
using PuppeteerLib;

namespace CharacterAI.Client
{
    public class CharacterAiClient : IDisposable
    {
        private string _browserExecutablePath;
        private readonly List<Guid> _heavyRequestsQueue = [];

        /// <summary>
        /// Browser : Usages
        /// </summary>
        //private readonly Dictionary<IBrowser, int> _browsersPool = new();
        private IBrowser _browser = null!;
        private IPage _page = null!;
        

        /// <summary>
        /// Create new integration with CharacterAI
        /// </summary>
        public CharacterAiClient(string? customBrowserDirectory = null, string? customBrowserExecutablePath = null)
        {
            var dir = string.IsNullOrWhiteSpace(customBrowserDirectory) ? null : customBrowserDirectory;
            var exe = string.IsNullOrWhiteSpace(customBrowserExecutablePath) ? null : customBrowserExecutablePath;

            _browserExecutablePath = exe ?? TryToDownloadBrowserAsync(dir ?? $"{CD}{SC}puppeteer-chrome").Result;
        }


        public async Task LaunchBrowserAsync()
        {            
            _browser = (await LaunchBrowserInstanceAsync(_browserExecutablePath))!;
            _page = await _browser.NewPageAsync();
            await _page.GoToAsync("https://plus.character.ai/search");
            bool ok = false;
            while (!ok)
                ok = await _page.TryToLeaveQueueAsync();
        }
        

        public void EnsureAllChromeInstancesAreKilled()
        {
            if (string.IsNullOrEmpty(_browserExecutablePath))
                throw new Exception("No browser path");

            try
            {
                string browserDir = _browserExecutablePath[..(_browserExecutablePath.LastIndexOf(SC))];
                var allProcessesInDir = Process.GetProcesses().Where(proc =>
                    proc.MainModule != null && proc.MainModule.FileName.StartsWith(browserDir));

                foreach (var proc in allProcessesInDir)
                {
                    try { proc.Kill(); }
                    catch (Exception e) { LogRed($"(Warning) Failed to kill \"{proc.Id}\"", e); }
                }
            }
            catch (Exception e)
            {
                LogRed("(Warning) Failed to kill browser instances", e);
            }
        }


        /// <summary>
        /// Send message and get response
        /// </summary>
        /// <returns>new CharacterResponse()</returns>
        public async Task<CharacterResponse> CallCharacterAsync(string characterId, string characterTgt, string historyId, string message = "", string? imagePath = null, string? primaryMsgUuId = null, string? parentMsgUuId = null, string authToken = "", bool plusMode = false)
        {
            var contentDynamic = BasicCallContent(characterId, characterTgt, message, imagePath, historyId);

            // Fetch new answer ("perform swipe").
            if (parentMsgUuId is not null)
            { // When parent_msg_id is present, character will generate new response for a last message.
                contentDynamic.parent_msg_uuid = parentMsgUuId;
            }
            // Or set new (swiped) answer as one to reply on.
            else if (primaryMsgUuId is not null)
            { // Provide primary_msg_id to point out which character's response you've chosen.
                contentDynamic.primary_msg_uuid = primaryMsgUuId;
                // (seen_msg_ids[] is also required, either it just won't work, but I didn't bother to collect
                //  every single swiped message, just fill it with chosen one)
                contentDynamic.seen_msg_uuids = new[] { primaryMsgUuId };
            }

            string sub = plusMode ? "plus" : "beta";
            string url = $"https://{sub}.character.ai/chat/streaming/";
            
            FetchResponse fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            if (fetchResponse.InQueue)
            {
                lock (_page)
                {
                    while (!_page.TryToLeaveQueueAsync().Result)
                        Task.Delay(10000).Wait();
                }

                fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            }

            var result = new CharacterResponse(fetchResponse);
            
            if (!fetchResponse.IsBlocked)
                return result;

            if (WaitForTurn() is not Guid requsetId)
                return result;

            try
            {
                var puppeteerResponse = await RequestPostWithDownloadAsync(requsetId, _browser.GetExeName(), url, authToken, contentDynamic);
                return new CharacterResponse(puppeteerResponse); // OK
            }
            catch (Exception e)
            {
                LogRed(e: e);
                return result;
            }
        }

        /// <summary>
        /// Get info about character
        /// </summary>
        /// <returns>new Character; can throw Exception</returns>
        public async Task<GetInfoResponse> GetInfoAsync(string characterId, string authToken = "", bool plusMode = false)
        {
            string sub = (plusMode) ? "plus" : "beta";
            string url = $"https://{sub}.character.ai/chat/character/info/";

            dynamic contentDynamic = new ExpandoObject();
            contentDynamic.external_id = characterId;

            FetchResponse fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            if (fetchResponse.InQueue)
            {
                lock (_page)
                {
                    while (!_page.TryToLeaveQueueAsync().Result)
                        Task.Delay(10000).Wait();
                }

                fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            }

            var result = new GetInfoResponse(fetchResponse);
            if (!fetchResponse.IsBlocked)
                return result;

            if (WaitForTurn() is not Guid requsetId)
                return result;

            try
            {
                var puppeteerResponse = await PostGotoRequestAsync(requsetId, _browser.GetExeName(), url, authToken, contentDynamic);
                return new GetInfoResponse(puppeteerResponse); // OK
            }
            catch (Exception e)
            {
                LogRed(e: e);
                return result;
            }
        }

        public async Task<string?> GetLastChatAsync(string characterId, string authToken = "", bool plusMode = false)
        {
            string sub = plusMode ? "plus" : "beta";
            string url = $"https://{sub}.character.ai/chat/history/continue/";

            dynamic contentDynamic = new ExpandoObject();
            contentDynamic.character_external_id = characterId;
            
            FetchResponse fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            if (fetchResponse.InQueue)
            {
                lock (_page)
                {
                    while (!_page.TryToLeaveQueueAsync().Result)
                        Task.Delay(10000).Wait();
                }

                fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            }

            if (fetchResponse.IsSuccessful && !fetchResponse.IsBlocked)
                return JsonConvert.DeserializeObject<dynamic>(fetchResponse.Content!)?.external_id;

            async Task<string?> FallbackOnCreateNewChat()
            {
                await Task.Delay(5000);
                return await CreateNewChatAsync(characterId, authToken, plusMode);
            }

            if (!fetchResponse.IsBlocked)
                return await FallbackOnCreateNewChat();

            if (WaitForTurn() is not Guid requsetId)
                return await FallbackOnCreateNewChat();

            try
            {
                var puppeteerResponse = await PostGotoRequestAsync(requsetId, _browser.GetExeName(), url, authToken, contentDynamic);
                return JsonConvert.DeserializeObject<dynamic>(puppeteerResponse.Content!)?.external_id; // OK
            }
            catch (Exception e)
            {
                LogRed(e: e);
                return await FallbackOnCreateNewChat();
            }
        }

        /// <summary>
        /// Create new chat with a character
        /// </summary>
        /// <returns>returns chat_history_id if successful; null if fails.</returns>
        public async Task<string?> CreateNewChatAsync(string characterId, string authToken = "", bool plusMode = false)
        {
            string sub = plusMode ? "plus" : "beta";
            string url = $"https://{sub}.character.ai/chat/history/create/";

            dynamic contentDynamic = new ExpandoObject();
            contentDynamic.character_external_id = characterId;

            FetchResponse fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            if (fetchResponse.InQueue)
            {
                lock (_page)
                {
                    while (!_page.TryToLeaveQueueAsync().Result)
                        Task.Delay(10000).Wait();
                }

                fetchResponse = await FetchRequestPostAsync(page: _page, url: url, authToken: authToken, data: contentDynamic);
            }
            if (fetchResponse.IsSuccessful && !fetchResponse.IsBlocked)
                return JsonConvert.DeserializeObject<dynamic>(fetchResponse.Content!)?.external_id;

            if (!fetchResponse.IsBlocked)
                return null;

            if (WaitForTurn() is not Guid requestId)
                return null;

            try
            {
                var puppeteerResponse = await PostGotoRequestAsync(requestId, _browser.GetExeName(), url, authToken, contentDynamic);
                return JsonConvert.DeserializeObject<dynamic>(puppeteerResponse.Content!)?.external_id; // OK
            }
            catch (Exception e)
            {
                LogRed(e: e);
                return null;
            }
        }

        // Search for a character
        public async Task<SearchResponse> SearchAsync(string query, string authToken = "", bool plusMode = false)
        {
            string sub = plusMode ? "plus" : "beta";
            string url = $"https://{sub}.character.ai/chat/characters/search/?query={query}";
            
            FetchResponse fetchResponse = await FetchRequestGetAsync(page: _page, url: url, authToken: authToken);
            if (fetchResponse.InQueue)
            {
                lock (_page)
                {
                    while (!_page.TryToLeaveQueueAsync().Result)
                        Task.Delay(10000).Wait();
                }

                fetchResponse = await FetchRequestGetAsync(page: _page, url: url, authToken: authToken);
            }

            var result = new SearchResponse(fetchResponse, query);
            if (!fetchResponse.IsBlocked)
                return result;

            if (WaitForTurn() is not Guid requsetId)
                return result;

            try
            {
                var puppeteerResponse = await GetGotoRequestAsync(requsetId, _browser.GetExeName(), url, authToken);
                return new SearchResponse(puppeteerResponse, query); // OK
            }
            catch (Exception e)
            {
                LogRed(e: e);
                return result;
            }
        }

        

        /// <summary>
        /// Here is listed the whole list of all known payload parameters.
        /// Some of these are useless, some seems to be not really used yet in actual API, some do simply have unknown purpose,
        /// thus they are either commented or set with default value taken from cai site.
        /// </summary>
        private static dynamic BasicCallContent(string characterId, string characterTgt, string msg, string? imgPath, string historyId)
        {
            dynamic content = new ExpandoObject();

            content.character_external_id = characterId;
            content.history_external_id = historyId;
            content.text = msg;
            content.tgt = characterTgt;

            if (!string.IsNullOrEmpty(imgPath))
            {
                content.image_description = "";
                content.image_description_type = "AUTO_IMAGE_CAPTIONING";
                content.image_origin_type = "UPLOADED";
                content.image_rel_path = imgPath;
            }

            // Unknown, unused and default params
            content.give_room_introductions = true;
            //initial_timeout : null
            //insert_beginning : null
            content.is_proactive = false;
            content.mock_response = false;
            //model_properties_version_keys : ""
            //model_properties_version_keys : ""
            //model_server_address : null
            content.num_candidates = 1;
            //override_prefix : null
            //override_rank : null
            //prefix_limit : null
            //prefix_token_limit : null
            //rank_candidates : null
            content.ranking_method = "random";
            //retry_last_user_msg_uuid : null
            content.CallCharacterAsyncstaging = false;
            content.stream_every_n_steps = 16;
            //stream_params : null
            //unsanitized_characters : null
            content.voice_enabled = false;

            return content;
        }

        private Guid? WaitForTurn()
        {
            Guid requestId;

            while (true)
            {
                requestId = Guid.NewGuid();
                lock (_heavyRequestsQueue)
                    if (!_heavyRequestsQueue.Contains(requestId)) break;
            }

            lock (_heavyRequestsQueue)
                _heavyRequestsQueue.Add(requestId);

            try
            {
                for (int i = 0; i <= 50; i++)
                {
                    lock (_heavyRequestsQueue)
                        if (_heavyRequestsQueue.Count == 0 || _heavyRequestsQueue[0] == requestId)
                            break;

                    if (i == 50)
                        return null;

                    Task.Delay(3000).Wait();
                }

                return requestId;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_heavyRequestsQueue)
                    _heavyRequestsQueue.Remove(requestId);
            }
        }

        #region IDisposable implementation with finalizer

        private bool _disposed;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);   
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                try
                {
                    EnsureAllChromeInstancesAreKilled();
                }
                catch { }

                try
                {
                    Directory.Delete($"{CD}{SC}puppeteer-temps", true);
                }
                catch { }

                try
                {
                    lock (_heavyRequestsQueue)
                        _heavyRequestsQueue.Clear();

                    _browser = null!;
                    _browserExecutablePath = null!;
                }
                catch { }
            }

            _disposed = true;
        }
        #endregion
    }

    public static class Ext
    {
        public static string GetExeName(this IBrowser browser)
            => browser.Process.MainModule!.FileName;
    }
}
