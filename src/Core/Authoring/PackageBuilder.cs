﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Security;
using NuGet.Resources;

namespace NuGet
{
    public class PackageBuilder : IPackageBuilder
    {
        private const string DefaultContentType = "application/octet";
        internal const string ManifestRelationType = "manifest";
        private readonly bool _includeEmptyDirectories;

        public PackageBuilder(string path, IPropertyProvider propertyProvider, bool includeEmptyDirectories)
            : this(path, Path.GetDirectoryName(path), propertyProvider, includeEmptyDirectories)
        {
        }

        public PackageBuilder(string path, string basePath, IPropertyProvider propertyProvider, bool includeEmptyDirectories)
            : this(includeEmptyDirectories)
        {
            using (Stream stream = File.OpenRead(path))
            {
                ReadManifest(stream, basePath, propertyProvider);
            }
        }

        public PackageBuilder(Stream stream, string basePath)
            : this(stream, basePath, NullPropertyProvider.Instance)
        {
        }

        public PackageBuilder(Stream stream, string basePath, IPropertyProvider propertyProvider)
            : this()
        {
            ReadManifest(stream, basePath, propertyProvider);
        }

        public PackageBuilder()
            : this(includeEmptyDirectories: false)
        {
        }

        private PackageBuilder(bool includeEmptyDirectories)
        {
            _includeEmptyDirectories = includeEmptyDirectories;
            Files = new Collection<IPackageFile>();
            DependencySets = new Collection<PackageDependencySet>();
            FrameworkReferences = new Collection<FrameworkAssemblyReference>();
            ContentFiles = new Collection<ManifestContentFiles>();
            PackageAssemblyReferences = new Collection<PackageReferenceSet>();
            Authors = new HashSet<string>();
            Owners = new HashSet<string>();
            Replaces = new HashSet<string>();
            Provides = new HashSet<string>();
            Conflicts = new HashSet<string>();
            Tags = new HashSet<string>();
        }

        public string Id
        {
            get;
            set;
        }

        public SemanticVersion Version
        {
            get;
            set;
        }

        public string Title
        {
            get;
            set;
        }

        public ISet<string> Authors
        {
            get;
            private set;
        }

        public ISet<string> Owners
        {
            get;
            private set;
        }

        public Uri IconUrl
        {
            get;
            set;
        }

        public Uri LicenseUrl
        {
            get;
            set;
        }

        public Uri ProjectUrl
        {
            get;
            set;
        }

        public bool RequireLicenseAcceptance
        {
            get;
            set;
        }

        public bool DevelopmentDependency
        {
            get;
            set;
        }

        public string Description
        {
            get;
            set;
        }

        public string Summary
        {
            get;
            set;
        }

        public string ReleaseNotes
        {
            get;
            set;
        }

        public string Language
        {
            get;
            set;
        }

        public ISet<string> Tags
        {
            get;
            private set;
        }

        public Uri ProjectSourceUrl { get; set; }
        public Uri PackageSourceUrl { get; set; }
        public Uri DocsUrl { get; set; }
        public Uri WikiUrl { get; set; }
        public Uri MailingListUrl { get; set; }
        public Uri BugTrackerUrl { get; set; }
        public ISet<string> Replaces { get; set; }
        public ISet<string> Provides { get; set; }
        public ISet<string> Conflicts { get; set; }

        public string SoftwareDisplayName { get; set; }
        public string SoftwareDisplayVersion { get; set; }

        public string Copyright
        {
            get;
            set;
        }

        public Collection<PackageDependencySet> DependencySets
        {
            get;
            private set;
        }

        public Collection<IPackageFile> Files
        {
            get;
            private set;
        }

        public Collection<FrameworkAssemblyReference> FrameworkReferences
        {
            get;
            private set;
        }

        /// <summary>
        /// ContentFiles section from the manifest for content v2
        /// </summary>
        public Collection<ManifestContentFiles> ContentFiles
        {
            get;
            private set;
        }

        public ICollection<PackageReferenceSet> PackageAssemblyReferences
        {
            get;
            private set;
        }

        IEnumerable<string> IPackageMetadata.Authors
        {
            get
            {
                return Authors;
            }
        }

        IEnumerable<string> IPackageMetadata.Owners
        {
            get
            {
                return Owners;
            }
        }

        string IPackageMetadata.Tags
        {
            get
            {
                return String.Join(" ", Tags);
            }
        }

        IEnumerable<string> IPackageMetadata.Replaces
        {
            get
            {
                return Replaces;
            }
        }

        IEnumerable<string> IPackageMetadata.Provides
        {
            get
            {
                return Provides;
            }
        }

        IEnumerable<string> IPackageMetadata.Conflicts
        {
            get
            {
                return Conflicts;
            }
        }

        IEnumerable<PackageDependencySet> IPackageMetadata.DependencySets
        {
            get
            {
                return DependencySets;
            }
        }

        IEnumerable<FrameworkAssemblyReference> IPackageMetadata.FrameworkAssemblies
        {
            get
            {
                return FrameworkReferences;
            }
        }

        public Version MinClientVersion
        {
            get;
            set;
        }

        public void Save(Stream stream)
        {
            // Make sure we're saving a valid package id
            PackageIdValidator.ValidatePackageId(Id);

            // Throw if the package doesn't contain any dependencies nor content
            if (!Files.Any() && !DependencySets.SelectMany(d => d.Dependencies).Any() && !FrameworkReferences.Any())
            {
                throw new InvalidOperationException(NuGetResources.CannotCreateEmptyPackage);
            }

            if (!ValidateSpecialVersionLength(Version))
            {
                throw new InvalidOperationException(NuGetResources.SemVerSpecialVersionTooLong);
            }

            ValidateDependencySets(Version, DependencySets);
            ValidateReferenceAssemblies(Files, PackageAssemblyReferences);

            using (Package package = Package.Open(stream, FileMode.Create))
            {
                // Validate and write the manifest
                WriteManifest(package, DetermineMinimumSchemaVersion(Files, DependencySets));

                // Write the files to the package
                WriteFiles(package);

                // Copy the metadata properties back to the package
                package.PackageProperties.Creator = SecurityElement.Escape(String.Join(",", Authors));
                package.PackageProperties.Description = SecurityElement.Escape(Description);
                package.PackageProperties.Identifier = Id;
                package.PackageProperties.Version = Version.ToString();
                package.PackageProperties.Language = SecurityElement.Escape(Language);
                package.PackageProperties.Keywords = SecurityElement.Escape(((IPackageMetadata)this).Tags);
                package.PackageProperties.Title = SecurityElement.Escape(Title);
                package.PackageProperties.LastModifiedBy = SecurityElement.Escape(CreatorInfo());
            }
        }

        private static string CreatorInfo()
        {
            List<string> creatorInfo = new List<string>();
            var assembly = typeof(PackageBuilder).Assembly;
            creatorInfo.Add(assembly.FullName);
            creatorInfo.Add(Environment.OSVersion.ToString());

            var attributes = assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), true);
            if (attributes.Length > 0)
            {
                var attribute = (System.Runtime.Versioning.TargetFrameworkAttribute)attributes[0];
                creatorInfo.Add(attribute.FrameworkDisplayName);
            }

            return String.Join(";", creatorInfo);
        }

        private static int DetermineMinimumSchemaVersion(
            Collection<IPackageFile> Files,
            Collection<PackageDependencySet> package)
        {
            if (HasContentFilesV2(Files) || HasIncludeExclude(package))
            {
                // version 5
                return ManifestVersionUtility.XdtTransformationVersion;
            }

            if (HasXdtTransformFile(Files))
            {
                // version 5
                return ManifestVersionUtility.XdtTransformationVersion;
            }

            if (RequiresV4TargetFrameworkSchema(Files))
            {
                // version 4
                return ManifestVersionUtility.TargetFrameworkSupportForDependencyContentsAndToolsVersion;
            }

            return ManifestVersionUtility.DefaultVersion;
        }

        private static bool RequiresV4TargetFrameworkSchema(ICollection<IPackageFile> files)
        {
            // check if any file under Content or Tools has TargetFramework defined
            bool hasContentOrTool = files.Any(
                f => f.TargetFramework != null &&
                     f.TargetFramework != VersionUtility.UnsupportedFrameworkName &&
                     (f.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                      f.Path.StartsWith(Constants.ToolsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

            if (hasContentOrTool)
            {
                return true;
            }

            // now check if the Lib folder has any empty framework folder
            bool hasEmptyLibFolder = files.Any(
                f => f.TargetFramework != null &&
                     f.Path.StartsWith(Constants.LibDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                     f.EffectivePath == Constants.PackageEmptyFileName);

            return hasEmptyLibFolder;
        }

        private static bool HasContentFilesV2(ICollection<IPackageFile> contentFiles)
        {
            return contentFiles.Any(file =>
                file.Path != null &&
                file.Path.StartsWith(Constants.ContentFilesDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasIncludeExclude(IEnumerable<PackageDependencySet> dependencySets)
        {
            return dependencySets.Any(dependencyGroup =>
                dependencyGroup.Dependencies
                   .Any(dependency => dependency.Include != null || dependency.Exclude != null));
        }

        private static bool HasXdtTransformFile(ICollection<IPackageFile> contentFiles)
        {
            return contentFiles.Any(file =>
                file.Path != null &&
                file.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (file.Path.EndsWith(".install.xdt", StringComparison.OrdinalIgnoreCase) ||
                 file.Path.EndsWith(".uninstall.xdt", StringComparison.OrdinalIgnoreCase)));
        }

        internal static void ValidateDependencySets(SemanticVersion version, IEnumerable<PackageDependencySet> dependencies)
        {
            if (version == null)
            {
                // We have independent validation for null-versions.
                return;
            }

            foreach (var dep in dependencies.SelectMany(s => s.Dependencies))
            {
                PackageIdValidator.ValidatePackageId(dep.Id);
            }

            if (String.IsNullOrEmpty(version.SpecialVersion))
            {
                // If we are creating a production package, do not allow any of the dependencies to be a prerelease version.
                var prereleaseDependency = dependencies.SelectMany(set => set.Dependencies).FirstOrDefault(IsPrereleaseDependency);
                if (prereleaseDependency != null)
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidPrereleaseDependency, prereleaseDependency.ToString()));
                }
            }
        }

        internal static void ValidateReferenceAssemblies(IEnumerable<IPackageFile> files, IEnumerable<PackageReferenceSet> packageAssemblyReferences)
        {
            var libFiles = new HashSet<string>(from file in files
                                               where !String.IsNullOrEmpty(file.Path) && file.Path.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                                               select Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var reference in packageAssemblyReferences.SelectMany(p => p.References))
            {
                if (!libFiles.Contains(reference) &&
                    !libFiles.Contains(reference + ".dll") &&
                    !libFiles.Contains(reference + ".exe") &&
                    !libFiles.Contains(reference + ".winmd"))
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidReference, reference));
                }
            }
        }

        private void ReadManifest(Stream stream, string basePath, IPropertyProvider propertyProvider)
        {
            // Deserialize the document and extract the metadata
            Manifest manifest = Manifest.ReadFrom(stream, propertyProvider, validateSchema: true);

            Populate(manifest.Metadata);

            // If there's no base path then ignore the files node
            if (basePath != null)
            {
                if (manifest.Files == null)
                {
                    AddFiles(basePath, string.Format("**{0}*", Path.DirectorySeparatorChar), null);
                }
                else
                {
                    PopulateFiles(basePath, manifest.Files);
                }
            }
        }

        public void Populate(ManifestMetadata manifestMetadata)
        {
            IPackageMetadata metadata = manifestMetadata;
            Id = metadata.Id;
            Version = metadata.Version;
            Title = metadata.Title;
            Authors.AddRange(metadata.Authors);
            Owners.AddRange(metadata.Owners);
            IconUrl = metadata.IconUrl;
            LicenseUrl = metadata.LicenseUrl;
            ProjectUrl = metadata.ProjectUrl;
            RequireLicenseAcceptance = metadata.RequireLicenseAcceptance;
            DevelopmentDependency = metadata.DevelopmentDependency;
            Description = metadata.Description;
            Summary = metadata.Summary;
            ReleaseNotes = metadata.ReleaseNotes;
            Language = metadata.Language;
            Copyright = metadata.Copyright;
            MinClientVersion = metadata.MinClientVersion;
            ContentFiles = new Collection<ManifestContentFiles>(manifestMetadata.ContentFiles);

            ProjectSourceUrl = metadata.ProjectSourceUrl;
            PackageSourceUrl = metadata.PackageSourceUrl;
            DocsUrl = metadata.DocsUrl;
            WikiUrl = metadata.WikiUrl;
            MailingListUrl = metadata.MailingListUrl;
            BugTrackerUrl = metadata.BugTrackerUrl;
            Replaces.AddRange(metadata.Replaces);
            Provides.AddRange(metadata.Provides);
            Conflicts.AddRange(metadata.Conflicts);

            SoftwareDisplayName = metadata.SoftwareDisplayName;
            SoftwareDisplayVersion = metadata.SoftwareDisplayVersion;

            if (metadata.Tags != null)
            {
                Tags.AddRange(ParseTags(metadata.Tags));
            }

            DependencySets.AddRange(metadata.DependencySets);
            FrameworkReferences.AddRange(metadata.FrameworkAssemblies);

            if (manifestMetadata.ReferenceSets != null)
            {
                PackageAssemblyReferences.AddRange(manifestMetadata.ReferenceSets.Select(r => new PackageReferenceSet(r)));
            }
        }

        public void PopulateFiles(string basePath, IEnumerable<ManifestFile> files)
        {
            foreach (var file in files)
            {
                AddFiles(basePath, file.Source, file.Target, file.Exclude);
            }
        }

        private void WriteManifest(Package package, int minimumManifestVersion)
        {
            Uri uri = UriUtility.CreatePartUri(Id + Constants.ManifestExtension);

            // Create the manifest relationship
            package.CreateRelationship(uri, TargetMode.Internal, Constants.PackageRelationshipNamespace + ManifestRelationType);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);

            using (Stream stream = packagePart.GetStream())
            {
                Manifest manifest = Manifest.Create(this);
                manifest.Save(stream, minimumManifestVersion);
            }
        }

        private void WriteFiles(Package package)
        {
            // Add files that might not come from expanding files on disk
            foreach (IPackageFile file in new HashSet<IPackageFile>(Files))
            {
                using (Stream stream = file.GetStream())
                {
                    try
                    {
                        CreatePart(package, file.Path, stream);
                    }
                    catch
                    {
                        Console.WriteLine(file.Path);
                        throw;
                    }
                }
            }

            foreach (var file in package.GetParts().GroupBy(s => s.Uri).Where(_ => _.Count() > 1))
            {
                Console.WriteLine(file.Key);
            }
        }

        private void AddFiles(string basePath, string source, string destination, string exclude = null)
        {
            List<PhysicalPackageFile> searchFiles = PathResolver.ResolveSearchPattern(basePath, source, destination, _includeEmptyDirectories).ToList();
            if (_includeEmptyDirectories)
            {
                // we only allow empty directories which are legit framework folders.
                // Folders for nuget v3 should be included here also since this part of nuget.core is still used
                // by nuget.exe 3.3.0.
                searchFiles.RemoveAll(file => file.TargetFramework == null
                                             && Path.GetFileName(file.TargetPath) == Constants.PackageEmptyFileName
                                             && !IsKnownV3Folder(file.TargetPath));
            }

            ExcludeFiles(searchFiles, basePath, exclude);

            if (!PathResolver.IsWildcardSearch(source) && !PathResolver.IsDirectoryPath(source) && !searchFiles.Any())
            {
                throw new FileNotFoundException(
                    String.Format(CultureInfo.CurrentCulture, NuGetResources.PackageAuthoring_FileNotFound, source));
            }


            Files.AddRange(searchFiles);
        }

        /// <summary>
        /// Returns true if the path uses a known v3 folder root.
        /// </summary>
        private static bool IsKnownV3Folder(string targetPath)
        {
            if (targetPath != null)
            {
                var parts = targetPath.Split(
                    new char[] { '\\', '/' },
                    StringSplitOptions.RemoveEmptyEntries);

                // exclude things in the root of the directory, this is not allowed
                // for any of the v3 folders.
                // example: an empty 'native' folder does not have a TxM and cannot be used.
                if (parts.Length > 1)
                {
                    var topLevelDirectory = parts.FirstOrDefault();

                    return KnownFoldersForV3.Any(folder =>
                        folder.Equals(topLevelDirectory, StringComparison.OrdinalIgnoreCase));
                }
            }

            return false;
        }

        /// <summary>
        /// Folders used in NuGet v3 that are not used in NuGet.Core
        /// </summary>
        private static IEnumerable<string> KnownFoldersForV3
        {
            get
            {
                yield return "contentFiles";
                yield return "ref";
                yield return "runtimes";
                yield return "native";
                yield return "analyzers";
                yield break;
            }
        }

        private static void ExcludeFiles(List<PhysicalPackageFile> searchFiles, string basePath, string exclude)
        {
            if (String.IsNullOrEmpty(exclude))
            {
                return;
            }

            // One or more exclusions may be specified in the file. Split it and prepend the base path to the wildcard provided.
            var exclusions = exclude.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in exclusions)
            {
                string wildCard = PathResolver.NormalizeWildcardForExcludedFiles(basePath, item);
                PathResolver.FilterPackageFiles(searchFiles, p => p.SourcePath, new[] { wildCard });
            }
        }

        private static void CreatePart(Package package, string path, Stream sourceStream)
        {
            if (PackageHelper.IsPackageManifest(path, package.PackageProperties.Identifier))
            {
                return;
            }

            Uri uri = UriUtility.CreatePartUri(path);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);
            using (Stream stream = packagePart.GetStream())
            {
                sourceStream.CopyTo(stream);
            }
        }

        /// <summary>
        /// Tags come in this format. tag1 tag2 tag3 etc..
        /// </summary>
        private static IEnumerable<string> ParseTags(string tags)
        {
            Debug.Assert(tags != null);
            return from tag in tags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                   select tag.Trim();
        }

        private static bool IsPrereleaseDependency(PackageDependency dependency)
        {
            var versionSpec = dependency.VersionSpec;
            if (versionSpec != null)
            {
                return (versionSpec.MinVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MinVersion.SpecialVersion)) ||
                       (versionSpec.MaxVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MaxVersion.SpecialVersion));
            }
            return false;
        }

        private static bool ValidateSpecialVersionLength(SemanticVersion version)
        {
            return version == null || version.SpecialVersion == null || version.SpecialVersion.Length <= 20;
        }
    }
}