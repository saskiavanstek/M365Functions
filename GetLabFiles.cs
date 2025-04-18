using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace M365LabFunctions
{
    public static class GetLabFiles
    {
        [FunctionName("GetLabFiles")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "repositories/{repoName}/labfiles")] HttpRequest req,
            string repoName,
            ILogger log)
        {
            log.LogInformation($"HTTP trigger function processed a request for repository: {repoName}");

            if (string.IsNullOrEmpty(repoName))
            {
                return new BadRequestObjectResult("Please provide a repository name in the route.");
            }

            string githubToken = Environment.GetEnvironmentVariable("GitHubToken"); // Configureer dit in de Function App settings

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("AzureFunctions"); // Vervang met je app naam indien gewenst
                if (!string.IsNullOrEmpty(githubToken))
                {
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
                }

                var basePath = "Instructions/Labs";
                var apiUrl = $"https://api.github.com/repos/IT-M365-Training/{repoName}/contents/{basePath}";

                try
                {
                    var response = await httpClient.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        log.LogError($"GitHub API error: {response.StatusCode} - {errorContent}");
                        return new StatusCodeResult((int)response.StatusCode);
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    var githubContents = JsonSerializer.Deserialize<List<GitHubApiContent>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    var labFilesStructure = new List<LabFileItem>();

                    if (githubContents != null)
                    {
                        foreach (var item in githubContents)
                        {
                            if (item.Type == "dir")
                            {
                                var subFiles = await GetLabFilesInDirectory(httpClient, repoName, item.Path, log);
                                if (subFiles.Any())
                                {
                                    labFilesStructure.Add(new LabFileItem { Name = $"**{item.Name}**", Path = null, IsFolder = true, SubItems = subFiles });
                                }
                            }
                            else if (item.Name.EndsWith(".md"))
                            {
                                labFilesStructure.Add(new LabFileItem { Name = item.Name.Replace(".md", ""), Path = item.Path, IsFolder = false, SubItems = null });
                            }
                        }

                        // Handle the case where MD files might be directly under Instructions/Labs (for MS-4015)
                        foreach (var item in githubContents.Where(c => c.Type == "file" && c.Name.EndsWith(".md")))
                        {
                            if (!labFilesStructure.Any(l => l.Path == item.Path && !l.IsFolder)) // Avoid duplicates and only add direct MD files
                            {
                                labFilesStructure.Add(new LabFileItem { Name = item.Name.Replace(".md", ""), Path = item.Path, IsFolder = false, SubItems = null });
                            }
                        }
                    }

                    return new OkObjectResult(labFilesStructure);
                }
                catch (Exception ex)
                {
                    log.LogError($"Error processing GitHub API request: {ex.Message}");
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                }
            }
        }

        private static async Task<List<LabFileItem>> GetLabFilesInDirectory(HttpClient httpClient, string repoName, string directoryPath, ILogger log)
        {
            var apiUrl = $"https://api.github.com/repos/IT-M365-Training/{repoName}/contents/{directoryPath}";
            var subFiles = new List<LabFileItem>();

            try
            {
                var response = await httpClient.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var githubContents = JsonSerializer.Deserialize<List<GitHubApiContent>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (githubContents != null)
                    {
                        foreach (var item in githubContents.Where(c => c.Type == "file" && c.Name.EndsWith(".md")))
                        {
                            subFiles.Add(new LabFileItem { Name = item.Name.Replace(".md", ""), Path = item.Path, IsFolder = false, SubItems = null });
                        }
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    log.LogError($"GitHub API error (subdirectory): {response.StatusCode} - {errorContent} for path: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error processing GitHub API request (subdirectory): {ex.Message} for path: {directoryPath}");
            }

            return subFiles;
        }

        public class GitHubApiContent
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("path")]
            public string Path { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("url")]
            public string Url { get; set; }
        }

        public class LabFileItem
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public bool IsFolder { get; set; }
            public List<LabFileItem> SubItems { get; set; }
        }
    }
}