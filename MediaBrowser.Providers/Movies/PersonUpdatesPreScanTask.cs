﻿using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Providers.Movies
{
    public class PersonUpdatesPreScanTask : IPeoplePrescanTask
    {
        /// <summary>
        /// The updates URL
        /// </summary>
        private const string UpdatesUrl = "http://api.themoviedb.org/3/person/changes?start_date={0}&api_key={1}&page={2}";

        /// <summary>
        /// The _HTTP client
        /// </summary>
        private readonly IHttpClient _httpClient;
        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;
        /// <summary>
        /// The _config
        /// </summary>
        private readonly IServerConfigurationManager _config;
        private readonly IJsonSerializer _json;

        /// <summary>
        /// Initializes a new instance of the <see cref="PersonUpdatesPreScanTask"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="config">The config.</param>
        public PersonUpdatesPreScanTask(ILogger logger, IHttpClient httpClient, IServerConfigurationManager config, IJsonSerializer json)
        {
            _logger = logger;
            _httpClient = httpClient;
            _config = config;
            _json = json;
        }

        protected readonly CultureInfo UsCulture = new CultureInfo("en-US");

        /// <summary>
        /// Runs the specified progress.
        /// </summary>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!_config.Configuration.EnableInternetProviders || !_config.Configuration.EnableTmdbUpdates)
            {
                progress.Report(100);
                return;
            }

            var path = TmdbPersonProvider.GetPersonsDataPath(_config.CommonApplicationPaths);

            Directory.CreateDirectory(path);

            var timestampFile = Path.Combine(path, "time.txt");

            var timestampFileInfo = new FileInfo(timestampFile);

            // Don't check for tvdb updates anymore frequently than 24 hours
            if (timestampFileInfo.Exists && (DateTime.UtcNow - timestampFileInfo.LastWriteTimeUtc).TotalDays < 1)
            {
                return;
            }

            // Find out the last time we queried tvdb for updates
            var lastUpdateTime = timestampFileInfo.Exists ? File.ReadAllText(timestampFile, Encoding.UTF8) : string.Empty;

            var existingDirectories = Directory.EnumerateDirectories(path).Select(Path.GetFileName).ToList();

            if (!string.IsNullOrEmpty(lastUpdateTime))
            {
                long lastUpdateTicks;

                if (long.TryParse(lastUpdateTime, NumberStyles.Any, UsCulture, out lastUpdateTicks))
                {
                    var lastUpdateDate = new DateTime(lastUpdateTicks, DateTimeKind.Utc);

                    // They only allow up to 14 days of updates
                    if ((DateTime.UtcNow - lastUpdateDate).TotalDays > 13)
                    {
                        lastUpdateDate = DateTime.UtcNow.AddDays(-13);
                    }

                    var updatedIds = await GetIdsToUpdate(lastUpdateDate, 1, cancellationToken).ConfigureAwait(false);

                    var existingDictionary = existingDirectories.ToDictionary(i => i, StringComparer.OrdinalIgnoreCase);

                    var idsToUpdate = updatedIds.Where(i => !string.IsNullOrWhiteSpace(i) && existingDictionary.ContainsKey(i));

                    await UpdatePeople(idsToUpdate, path, progress, cancellationToken).ConfigureAwait(false);
                }
            }

            File.WriteAllText(timestampFile, DateTime.UtcNow.Ticks.ToString(UsCulture), Encoding.UTF8);
            progress.Report(100);
        }

        /// <summary>
        /// Gets the ids to update.
        /// </summary>
        /// <param name="lastUpdateTime">The last update time.</param>
        /// <param name="page">The page.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IEnumerable{System.String}}.</returns>
        private async Task<IEnumerable<string>> GetIdsToUpdate(DateTime lastUpdateTime, int page, CancellationToken cancellationToken)
        {
            var hasMorePages = false;
            var list = new List<string>();

            // First get last time
            using (var stream = await _httpClient.Get(new HttpRequestOptions
            {
                Url = string.Format(UpdatesUrl, lastUpdateTime.ToString("yyyy-MM-dd"), MovieDbProvider.ApiKey, page),
                CancellationToken = cancellationToken,
                EnableHttpCompression = true,
                ResourcePool = MovieDbProvider.Current.MovieDbResourcePool,
                AcceptHeader = MovieDbProvider.AcceptHeader

            }).ConfigureAwait(false))
            {
                var obj = _json.DeserializeFromStream<RootObject>(stream);

                var data = obj.results.Select(i => i.id.ToString(UsCulture));

                list.AddRange(data);

                hasMorePages = page < obj.total_pages;
            }

            if (hasMorePages)
            {
                var more = await GetIdsToUpdate(lastUpdateTime, page + 1, cancellationToken).ConfigureAwait(false);

                list.AddRange(more);
            }

            return list;
        }

        /// <summary>
        /// Updates the people.
        /// </summary>
        /// <param name="ids">The ids.</param>
        /// <param name="peopleDataPath">The people data path.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task UpdatePeople(IEnumerable<string> ids, string peopleDataPath, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var list = ids.ToList();
            var numComplete = 0;

            foreach (var id in list)
            {
                try
                {
                    await UpdatePerson(id, peopleDataPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error updating tmdb person id {0}", ex, id);
                }

                numComplete++;
                double percent = numComplete;
                percent /= list.Count;
                percent *= 100;

                progress.Report(percent);
            }
        }

        /// <summary>
        /// Updates the person.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="peopleDataPath">The people data path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private Task UpdatePerson(string id, string peopleDataPath, CancellationToken cancellationToken)
        {
            _logger.Info("Updating person from tmdb " + id);

            var personDataPath = Path.Combine(peopleDataPath, id);

            Directory.CreateDirectory(peopleDataPath);

            return TmdbPersonProvider.Current.DownloadPersonInfo(id, personDataPath, cancellationToken);
        }

        class Result
        {
            public int id { get; set; }
            public bool? adult { get; set; }
        }

        class RootObject
        {
            public List<Result> results { get; set; }
            public int page { get; set; }
            public int total_pages { get; set; }
            public int total_results { get; set; }

            public RootObject()
            {
                results = new List<Result>();
            }
        }
    }
}
