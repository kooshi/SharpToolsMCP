using ModelContextProtocol;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Xml.Linq;

namespace SharpTools.Tools.Mcp.Tools;

// Marker class for ILogger<T> category specific to PackageTools
public class PackageToolsLogCategory { }

[McpServerToolType]
public static class PackageTools {
    // Disabled for now, needs to handle dependencies and reloading solution
    //[McpServerTool(Name = ToolHelpers.SharpToolPrefix + nameof(AddOrModifyNugetPackage), Idempotent = false, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Adds or modifies a NuGet package in a project.")]
    public static async Task<string> AddOrModifyNugetPackage(
        ILogger<PackageToolsLogCategory> logger,
        ISolutionManager solutionManager,
        IDocumentOperationsService documentOperations,
        string projectName,
        string nugetPackageId,
        [Description("The version of the NuGet package or 'latest' for latest")] string? version,
        CancellationToken cancellationToken = default) {
        return await ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync(async () => {
            // Validate parameters
            ErrorHandlingHelpers.ValidateStringParameter(projectName, nameof(projectName), logger);
            ErrorHandlingHelpers.ValidateStringParameter(nugetPackageId, nameof(nugetPackageId), logger);
            logger.LogInformation("Adding/modifying NuGet package '{PackageId}' {Version} to {ProjectPath}",
                nugetPackageId, version ?? "latest", projectName);

            if (string.IsNullOrEmpty(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase)) {
                version = null; // Treat 'latest' as null for processing
            }

            int indexOfParen = projectName.IndexOf('(');
            string projectNameNormalized = indexOfParen == -1
                ? projectName.Trim()
                : projectName[..indexOfParen].Trim();

            var project = solutionManager.GetProjects().FirstOrDefault(
                p => p.Name == projectName
                || p.AssemblyName == projectName
                || p.Name == projectNameNormalized);

            if (project == null) {
                logger.LogError("Project '{ProjectName}' not found in the loaded solution", projectName);
                throw new McpException($"Project '{projectName}' not found in the solution.");
            }

            // Validate the package exists
            var packageExists = await ValidatePackageAsync(nugetPackageId, version, logger, cancellationToken);
            if (!packageExists) {
                throw new McpException($"Package '{nugetPackageId}' {(string.IsNullOrEmpty(version) ? "" : $"with version {version} ")}was not found on NuGet.org.");
            }

            // If no version specified, get the latest version
            if (string.IsNullOrEmpty(version)) {
                version = await GetLatestVersionAsync(nugetPackageId, logger, cancellationToken);
                logger.LogInformation("Using latest version {Version} for package {PackageId}", version, nugetPackageId);
            }

            ErrorHandlingHelpers.ValidateFileExists(project.FilePath, logger);
            var projectPath = project.FilePath!;

            // Detect package format and add/update accordingly
            var packageFormat = LegacyNuGetPackageReader.DetectPackageFormat(projectPath);
            var action = "added";
            var projectPackages = LegacyNuGetPackageReader.GetPackagesForProject(projectPath);

            // Check if package already exists to determine if we're adding or updating
            var existingPackage = projectPackages.Packages.FirstOrDefault(p =>
                string.Equals(p.PackageId, nugetPackageId, StringComparison.OrdinalIgnoreCase));

            if (existingPackage != null) {
                action = "updated";
                logger.LogInformation("Package {PackageId} already exists with version {OldVersion}, updating to {NewVersion}",
                    nugetPackageId, existingPackage.Version, version);
            }

            // Update the project file based on the package format
            if (packageFormat == LegacyNuGetPackageReader.PackageFormat.PackageReference) {
                await UpdatePackageReferenceAsync(projectPath, nugetPackageId, version, existingPackage != null, documentOperations, logger, cancellationToken);
            } else {
                var packagesConfigPath = projectPackages.PackagesConfigPath ?? throw new McpException("packages.config path not found.");
                await UpdatePackagesConfigAsync(packagesConfigPath, nugetPackageId, version, existingPackage != null, documentOperations, logger, cancellationToken);
            }

            logger.LogInformation("Package {PackageId} {Action} with version {Version}", nugetPackageId, action, version);
            return $"The package {nugetPackageId} has been {action} with version {version}. You must perform a `{(packageFormat == LegacyNuGetPackageReader.PackageFormat.PackageReference ? "dotnet restore" : "nuget restore")}` and then reload the solution.";
        }, logger, nameof(AddOrModifyNugetPackage), cancellationToken);
    }
    private static async Task<bool> ValidatePackageAsync(string packageId, string? version, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken) {
        try {
            logger.LogInformation("Validating package {PackageId} {Version} on NuGet.org",
                packageId, version ?? "latest");

            var nugetLogger = NullLogger.Instance;

            // Create repository
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken);

            // Get package metadata
            var packages = await resource.GetMetadataAsync(
                packageId,
                includePrerelease: true,
                includeUnlisted: false,
                sourceCacheContext: new SourceCacheContext(),
                nugetLogger,
                cancellationToken);

            if (!packages.Any())
                return false; // Package doesn't exist

            if (string.IsNullOrEmpty(version))
                return true; // Just checking existence

            // Validate specific version
            var targetVersion = NuGetVersion.Parse(version);
            return packages.Any(p => p.Identity.Version.Equals(targetVersion));
        } catch (Exception ex) {
            logger.LogError(ex, "Error validating NuGet package {PackageId} {Version}", packageId, version);
            throw new McpException($"Failed to validate NuGet package '{packageId}': {ex.Message}");
        }
    }

    private static async Task<string> GetLatestVersionAsync(string packageId, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken) {
        try {
            var nugetLogger = NullLogger.Instance;

            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken);

            var packages = await resource.GetMetadataAsync(
                packageId,
                includePrerelease: false, // Only stable versions for the latest
                includeUnlisted: false,
                sourceCacheContext: new SourceCacheContext(),
                nugetLogger,
                cancellationToken);

            var latestPackage = packages
                .OrderByDescending(p => p.Identity.Version)
                .FirstOrDefault();

            if (latestPackage == null) {
                throw new McpException($"No stable versions found for package '{packageId}'.");
            }

            return latestPackage.Identity.Version.ToString();
        } catch (Exception ex) {
            logger.LogError(ex, "Error getting latest version for NuGet package {PackageId}", packageId);
            throw new McpException($"Failed to get latest version for NuGet package '{packageId}': {ex.Message}");
        }
    }

    private static async Task UpdatePackageReferenceAsync(
        string projectPath,
        string packageId,
        string version,
        bool isUpdate,
        IDocumentOperationsService documentOperations,
        Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken cancellationToken) {

        try {
            var (projectContent, _) = await documentOperations.ReadFileAsync(projectPath, false, cancellationToken);
            var xDoc = XDocument.Parse(projectContent);

            // Find ItemGroup that contains PackageReference elements or create a new one
            var itemGroup = xDoc.Root?.Elements("ItemGroup")
                .FirstOrDefault(ig => ig.Elements("PackageReference").Any());

            if (itemGroup == null) {
                // Create a new ItemGroup for PackageReferences
                itemGroup = new XElement("ItemGroup");
                xDoc.Root?.Add(itemGroup);
            }

            // Find existing package reference
            var existingPackage = itemGroup.Elements("PackageReference")
                .FirstOrDefault(pr => string.Equals(pr.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase));

            if (existingPackage != null) {
                // Update existing package
                var versionAttr = existingPackage.Attribute("Version");
                if (versionAttr != null) {
                    versionAttr.Value = version;
                } else {
                    // Version might be in a child element
                    var versionElement = existingPackage.Element("Version");
                    if (versionElement != null) {
                        versionElement.Value = version;
                    } else {
                        // Add version as attribute if neither exists
                        existingPackage.Add(new XAttribute("Version", version));
                    }
                }
            } else {
                // Add new package reference
                var packageRef = new XElement("PackageReference",
                    new XAttribute("Include", packageId),
                    new XAttribute("Version", version));

                itemGroup.Add(packageRef);
            }

            // Save the updated project file
            await documentOperations.WriteFileAsync(projectPath, xDoc.ToString(), true, cancellationToken,
                $"{(isUpdate ? "Updated" : "Added")} NuGet package {packageId} with version {version}");
        } catch (Exception ex) {
            logger.LogError(ex, "Error updating PackageReference in project file {ProjectPath}", projectPath);
            throw new McpException($"Failed to update PackageReference in project file: {ex.Message}");
        }
    }

    private static async Task UpdatePackagesConfigAsync(
        string? packagesConfigPath,
        string packageId,
        string version,
        bool isUpdate,
        IDocumentOperationsService documentOperations,
        Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken cancellationToken) {

        if (string.IsNullOrEmpty(packagesConfigPath)) {
            throw new McpException("packages.config path is null or empty.");
        }

        try {
            // Check if packages.config exists, if not create it
            bool fileExists = documentOperations.FileExists(packagesConfigPath);
            XDocument xDoc;

            if (fileExists) {
                var (content, _) = await documentOperations.ReadFileAsync(packagesConfigPath, false, cancellationToken);
                xDoc = XDocument.Parse(content);
            } else {
                // Create a new packages.config file
                xDoc = new XDocument(
                    new XElement("packages",
                        new XAttribute("xmlns", "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd")));
            }

            // Find existing package
            var packageElement = xDoc.Root?.Elements("package")
                .FirstOrDefault(p => string.Equals(p.Attribute("id")?.Value, packageId, StringComparison.OrdinalIgnoreCase));

            if (packageElement != null) {
                // Update existing package
                packageElement.Attribute("version")!.Value = version;
            } else {
                // Add new package entry
                var newPackage = new XElement("package",
                    new XAttribute("id", packageId),
                    new XAttribute("version", version),
                    new XAttribute("targetFramework", "net40")); // Default target framework

                xDoc.Root?.Add(newPackage);
            }

            // Save the updated packages.config
            await documentOperations.WriteFileAsync(packagesConfigPath, xDoc.ToString(), true, cancellationToken,
                $"{(isUpdate ? "Updated" : "Added")} NuGet package {packageId} with version {version} in packages.config");
        } catch (Exception ex) {
            logger.LogError(ex, "Error updating packages.config at {PackagesConfigPath}", packagesConfigPath);
            throw new McpException($"Failed to update packages.config: {ex.Message}");
        }
    }
}