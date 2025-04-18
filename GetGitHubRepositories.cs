using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

public static class GetGitHubRepositories
{
    [Function("GetGitHubRepositories")]
    public static async Task<HttpResponseData> RunGetRepositories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "repos")] HttpRequestData req,
        FunctionContext executionContext)
    {
        var log = executionContext.GetLogger("GetGitHubRepositories");
        log.LogInformation("C# HTTP trigger function processed a request to get GitHub repositories.");

        var config = new ConfigurationBuilder()
            .SetBasePath(Environment.CurrentDirectory)
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        string githubUsernameOrOrg = config["GitHubUsernameOrOrg"];
        string githubToken = config["GitHubToken"];

        if (string.IsNullOrEmpty(githubUsernameOrOrg))
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            response.WriteString("Please configure the GitHubUsernameOrOrg application setting.");
            return response;
        }

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("YourAppName/1.0");

            if (!string.IsNullOrEmpty(githubToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", githubToken);
            }

            var url = $"https://api.github.com/users/{githubUsernameOrOrg}/repos";
            if (string.IsNullOrEmpty(githubToken) && githubUsernameOrOrg.Contains("/")) // Assuming it's an org if it has a slash
            {
                url = $"https://api.github.com/orgs/{githubUsernameOrOrg}/repos";
            }

            try
            {
                var response = await httpClient.GetAsync(url);
                var functionResponse = req.CreateResponse(response.StatusCode);
                functionResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await functionResponse.WriteStringAsync(await response.Content.ReadAsStringAsync());
                return functionResponse;
            }
            catch (HttpRequestException ex)
            {
                log.LogError($"HTTP request to GitHub failed: {ex.Message}");
                var functionResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                functionResponse.WriteString("Failed to connect to GitHub.");
                return functionResponse;
            }
            catch (JsonException ex)
            {
                log.LogError($"Failed to deserialize GitHub response: {ex.Message}");
                var functionResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                functionResponse.WriteString("Failed to process GitHub response.");
                return functionResponse;
            }
        }
    }

    [Function("GetRepositoryLabFiles")]
    public static async Task<HttpResponseData> RunGetLabFiles(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "repositories/{repoName}/labfiles")] HttpRequestData req,
        string repoName,
        FunctionContext executionContext)
    {
        var log = executionContext.GetLogger("GetRepositoryLabFiles");
        log.LogInformation($"HTTP trigger function processed a request for repository: {repoName} lab files.");

        if (string.IsNullOrEmpty(repoName))
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            response.WriteString("Please provide a repository name in the route.");
            return response;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(Environment.CurrentDirectory)
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        string githubToken = config["GitHubToken"]; // Configure this in local.settings.json or environment variables

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("AzureFunctions"); // Replace with your app name if desired
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
                    return req.CreateResponse(response.StatusCode);
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

                var jsonResponse = JsonSerializer.Serialize(labFilesStructure);
                var okResponse = req.CreateResponse(HttpStatusCode.OK);
                okResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await okResponse.WriteStringAsync(jsonResponse);
                return okResponse;
            }
            catch (Exception ex)
            {
                log.LogError($"Error processing GitHub API request: {ex.Message}");
                return req.CreateResponse(HttpStatusCode.InternalServerError);
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