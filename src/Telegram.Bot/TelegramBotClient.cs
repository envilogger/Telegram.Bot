using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using File = Telegram.Bot.Types.File;

namespace Telegram.Bot
{
    /// <summary>
    /// A client to use the Telegram Bot API
    /// </summary>
    public class TelegramBotClient : ITelegramBotClient
    {
        private const string BaseUrl = "https://api.telegram.org";
        private const string BaseFileUrl = "https://api.telegram.org/file/bot";

        private readonly string _token;
        private readonly string _baseRequestUrl;
        private readonly string _baseFileUrl;

        private readonly HttpClient _httpClient;

        /// <inheritdoc/>
        public int BotId { get; }

        #region Config Properties

        /// <summary>
        /// Timeout for requests
        /// </summary>
        public TimeSpan Timeout
        {
            get => _httpClient.Timeout;
            set => _httpClient.Timeout = value;
        }

        #endregion Config Properties

        #region Events

        /// <summary>
        /// Occurs before sending a request to API
        /// </summary>
        public event EventHandler<ApiRequestEventArgs>? MakingApiRequest;

        /// <summary>
        /// Occurs after receiving the response to an API request
        /// </summary>
        public event EventHandler<ApiResponseEventArgs>? ApiResponseReceived;

        #endregion

        /// <summary>
        /// Create a new <see cref="TelegramBotClient"/> instance.
        /// </summary>
        /// <param name="token">API token</param>
        /// <param name="httpClient">A custom <see cref="HttpClient"/></param>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="token"/> format is invalid
        /// </exception>
        public TelegramBotClient(string token, HttpClient? httpClient = null)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
            string[] parts = _token.Split(':');
            if (parts.Length > 1 && int.TryParse(parts[0], out var id))
            {
                BotId = id;
            }
            else
            {
                throw new ArgumentException(
                    "Invalid format. A valid token looks like \"1234567:4TT8bAc8GHUspu3ERYn-KGcvsvGB9u_n4ddy\".",
                    nameof(token)
                );
            }

            _baseRequestUrl = $"{BaseUrl}{_token}/";
            _baseFileUrl = $"{BaseFileUrl}{_token}/";
            _httpClient = httpClient ?? new HttpClient();
        }

        #region Helpers

        /// <inheritdoc />
        public async Task<TResponse> MakeRequestAsync<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            var url = _baseRequestUrl + request.MethodName;

            var httpRequest = new HttpRequestMessage(request.Method, url)
            {
                Content = request.ToHttpContent()
            };

            var reqDataArgs = new ApiRequestEventArgs(request.MethodName, httpRequest.Content);
            MakingApiRequest?.Invoke(this, reqDataArgs);

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException e)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new ApiRequestException("Request timed out", 408, e);
            }

            // required since user might be able to set new status code using following event arg
            var actualResponseStatusCode = httpResponse.StatusCode;
            string responseJson = await httpResponse.Content.ReadAsStringAsync()
                .ConfigureAwait(false);

            ApiResponseReceived?.Invoke(this, new ApiResponseEventArgs(httpResponse, reqDataArgs));

            switch (actualResponseStatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.BadRequest when !string.IsNullOrWhiteSpace(responseJson):
                case HttpStatusCode.Forbidden when !string.IsNullOrWhiteSpace(responseJson):
                case HttpStatusCode.Conflict when !string.IsNullOrWhiteSpace(responseJson):
                    // Do NOT throw here, an ApiRequestException will be thrown next
                    break;
                default:
                    httpResponse.EnsureSuccessStatusCode();
                    break;
            }

            var apiResponse =
                JsonConvert.DeserializeObject<ApiResponse<TResponse>>(responseJson)
                ?? new ApiResponse<TResponse> // ToDo is required? unit test
                {
                    Ok = false,
                    Description = "No response received"
                };

            if (!apiResponse.Ok)
                throw ApiExceptionParser.Parse(apiResponse);

            return apiResponse.Result;
        }

        /// <summary>
        /// Test the API token
        /// </summary>
        /// <returns><c>true</c> if token is valid</returns>
        public async Task<bool> TestApiAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await MakeRequestAsync(new GetMeRequest(), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (ApiRequestException e)
                when (e.ErrorCode == 401)
            {
                return false;
            }
        }

        #endregion Helpers

        /// <inheritdoc />
        public async Task DownloadFileAsync(
            string filePath,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length < 2)
            {
                throw new ArgumentException("Invalid file path", nameof(filePath));
            }

            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            var fileUri = new Uri($"{_baseFileUrl}{filePath}");

            var response = await _httpClient
                .GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using (response)
            {
                await response.Content.CopyToAsync(destination)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<File> GetInfoAndDownloadFileAsync(
            string fileId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            var file = await MakeRequestAsync(new GetFileRequest(fileId), cancellationToken)
                .ConfigureAwait(false);

            await DownloadFileAsync(file.FilePath!, destination, cancellationToken)
                .ConfigureAwait(false);

            return file;
        }
    }
}
