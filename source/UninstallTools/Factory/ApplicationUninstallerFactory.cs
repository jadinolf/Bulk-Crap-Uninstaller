﻿/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Klocman.Extensions;
using Klocman.Forms.Tools;
using Klocman.IO;
using Klocman.Tools;
using UninstallTools.Factory.InfoAdders;
using UninstallTools.Properties;
using UninstallTools.Startup;

namespace UninstallTools.Factory
{
    public static class ApplicationUninstallerFactory
    {
        private static readonly InfoAdderManager InfoAdder = new InfoAdderManager();

        public static IList<ApplicationUninstallerEntry> GetUninstallerEntries(ListGenerationProgress.ListGenerationCallback callback)
        {
            const int totalStepCount = 8;
            var currentStep = 1;

            var concurrentFactory = new ConcurrentApplicationFactory(GetMiscUninstallerEntries);

            try
            {
                // Find msi products ---------------------------------------------------------------------------------------
                var msiProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_MSI);
                callback(msiProgress);
                var msiGuidCount = 0;
                var msiProducts = MsiTools.MsiEnumProducts().DoForEach(x =>
                {
                    msiProgress.Inner = new ListGenerationProgress(0, -1, string.Format(Localisation.Progress_MSI_sub, ++msiGuidCount));
                    callback(msiProgress);
                }).ToList();

                // Run some factories in a separate thread -----------------------------------------------------------------
                concurrentFactory.Start();

                // Find stuff mentioned in registry ------------------------------------------------------------------------
                List<ApplicationUninstallerEntry> registryResults;
                if (UninstallToolsGlobalConfig.ScanRegistry)
                {
                    var regProgress = new ListGenerationProgress(currentStep++, totalStepCount,
                        Localisation.Progress_Registry);
                    callback(regProgress);
                    var registryFactory = new RegistryFactory(msiProducts);
                    registryResults = registryFactory.GetUninstallerEntries(report =>
                    {
                        regProgress.Inner = report;
                        callback(regProgress);
                    }).ToList();

                    // Fill in instal llocations for the drive search
                    if (UninstallToolsGlobalConfig.UninstallerFactoryCache != null)
                        ApplyCache(registryResults, UninstallToolsGlobalConfig.UninstallerFactoryCache, InfoAdder);

                    var installLocAddProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_GatherUninstallerInfo);
                    callback(installLocAddProgress);
                    var installLocAddCount = 0;
                    foreach (var result in registryResults)
                    {
                        installLocAddProgress.Inner = new ListGenerationProgress(installLocAddCount++, registryResults.Count, result.DisplayName ?? string.Empty);
                        callback(installLocAddProgress);

                        InfoAdder.AddMissingInformation(result, true);
                    }
                }
                else
                {
                    registryResults = new List<ApplicationUninstallerEntry>();
                }

                // Look for entries on drives, based on info in registry. ----------------------------------------------------
                // Will introduce duplicates to already detected stuff. Need to check for duplicates with other entries later.
                List<ApplicationUninstallerEntry> driveResults;
                if (UninstallToolsGlobalConfig.ScanDrives)
                {
                    var driveProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_DriveScan);
                    callback(driveProgress);
                    var driveFactory = new DirectoryFactory(registryResults);
                    driveResults = driveFactory.GetUninstallerEntries(report =>
                    {
                        driveProgress.Inner = report;
                        callback(driveProgress);
                    }).ToList();
                }
                else
                {
                    driveResults = new List<ApplicationUninstallerEntry>();
                }

                // Join up with the thread ----------------------------------------------------------------------------------
                var miscProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_AppStores);
                callback(miscProgress);
                var otherResults = concurrentFactory.GetResults(callback, miscProgress);

                // Handle duplicate entries ----------------------------------------------------------------------------------
                var mergeProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_Merging);
                callback(mergeProgress);
                var mergedResults = registryResults.ToList();
                mergedResults = MergeResults(mergedResults, otherResults, report =>
                {
                    mergeProgress.Inner = report;
                    report.TotalCount *= 2;
                    report.Message = Localisation.Progress_Merging_Stores;
                    callback(mergeProgress);
                });
                // Make sure to merge driveResults last
                mergedResults = MergeResults(mergedResults, driveResults, report =>
                {
                    mergeProgress.Inner = report;
                    report.CurrentCount += report.TotalCount;
                    report.TotalCount *= 2;
                    report.Message = Localisation.Progress_Merging_Drives;
                    callback(mergeProgress);
                });

                // Fill in any missing information -------------------------------------------------------------------------
                if (UninstallToolsGlobalConfig.UninstallerFactoryCache != null)
                    ApplyCache(mergedResults, UninstallToolsGlobalConfig.UninstallerFactoryCache, InfoAdder);

                var infoAddProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_GeneratingInfo);
                callback(infoAddProgress);
                var infoAddCount = 0;
                foreach (var result in mergedResults)
                {
                    infoAddProgress.Inner = new ListGenerationProgress(infoAddCount++, registryResults.Count, result.DisplayName ?? string.Empty);
                    callback(infoAddProgress);

                    InfoAdder.AddMissingInformation(result);
                    result.IsValid = CheckIsValid(result, msiProducts);
                }

                // Cache missing information to speed up future scans
                if (UninstallToolsGlobalConfig.UninstallerFactoryCache != null)
                {
                    foreach (var entry in mergedResults)
                        UninstallToolsGlobalConfig.UninstallerFactoryCache.TryCacheItem(entry);

                    try
                    {
                        UninstallToolsGlobalConfig.UninstallerFactoryCache.Save();
                    }
                    catch (SystemException e)
                    {
                        Console.WriteLine(@"Failed to save cache: " + e);
                    }
                }

                // Detect startups and attach them to uninstaller entries ----------------------------------------------------
                var startupsProgress = new ListGenerationProgress(currentStep, totalStepCount, Localisation.Progress_Startup);
                callback(startupsProgress);
                var i = 0;
                var startupEntries = new List<StartupEntryBase>();
                foreach (var factory in StartupManager.Factories)
                {
                    startupsProgress.Inner = new ListGenerationProgress(i++, StartupManager.Factories.Count, factory.Key);
                    callback(startupsProgress);
                    try
                    {
                        startupEntries.AddRange(factory.Value());
                    }
                    catch (Exception ex)
                    {
                        PremadeDialogs.GenericError(ex);
                    }
                }

                startupsProgress.Inner = new ListGenerationProgress(1, 1, Localisation.Progress_Merging);
                callback(startupsProgress);
                try
                {
                    AttachStartupEntries(mergedResults, startupEntries);
                }
                catch (Exception ex)
                {
                    PremadeDialogs.GenericError(ex);
                }

                return mergedResults;
            }
            finally
            {
                concurrentFactory.Dispose();
            }
        }

        internal static List<ApplicationUninstallerEntry> MergeResults(ICollection<ApplicationUninstallerEntry> baseEntries,
            ICollection<ApplicationUninstallerEntry> newResults, ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            // Add all of the base results straight away
            var results = new List<ApplicationUninstallerEntry>(baseEntries);
            var progress = 0;
            foreach (var entry in newResults)
            {
                progressCallback?.Invoke(new ListGenerationProgress(progress++, newResults.Count, null));

                var matchedEntry = baseEntries.Select(x => new { x, score = ApplicationEntryTools.AreEntriesRelated(x, entry) })
                    .Where(x => x.score >= 1)
                    .OrderByDescending(x => x.score)
                    .Select(x => x.x)
                    .FirstOrDefault();

                if (matchedEntry != null)
                {
                    // Prevent setting incorrect UninstallerType
                    if (matchedEntry.UninstallPossible)
                        entry.UninstallerKind = UninstallerType.Unknown;

                    InfoAdder.CopyMissingInformation(matchedEntry, entry);
                    continue;
                }

                // If the entry failed to match to anything, add it to the results
                results.Add(entry);
            }

            return results;
        }

        private static void ApplyCache(ICollection<ApplicationUninstallerEntry> baseEntries, ApplicationUninstallerFactoryCache cache, InfoAdderManager infoAdder)
        {
            var hits = 0;
            foreach (var entry in baseEntries)
            {
                var matchedEntry = cache.TryGetCachedItem(entry);
                if (matchedEntry != null)
                {
                    infoAdder.CopyMissingInformation(entry, matchedEntry);
                    hits++;
                }
                else
                {
                    Debug.WriteLine("Cache miss: " + entry.DisplayName);
                }
            }
            Console.WriteLine($@"Cache hits: {hits}/{baseEntries.Count}");
        }

        private static bool CheckIsValid(ApplicationUninstallerEntry target, IEnumerable<Guid> msiProducts)
        {
            if (String.IsNullOrEmpty(target.UninstallerFullFilename))
                return false;

            bool isPathRooted;
            try
            {
                isPathRooted = Path.IsPathRooted(target.UninstallerFullFilename);
            }
            catch (ArgumentException)
            {
                isPathRooted = false;
            }

            if (isPathRooted && File.Exists(target.UninstallerFullFilename))
                return true;

            if (target.UninstallerKind == UninstallerType.Msiexec)
                return msiProducts.Contains(target.BundleProviderKey);

            return !isPathRooted;
        }

        private static List<ApplicationUninstallerEntry> GetMiscUninstallerEntries(ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            var otherResults = new List<ApplicationUninstallerEntry>();

            var miscFactories = ReflectionTools.GetTypesImplementingBase<IIndependantUninstallerFactory>()
                .Attempt(Activator.CreateInstance)
                .Cast<IIndependantUninstallerFactory>()
                .Where(x => x.IsEnabled())
                .ToList();

            var progress = 0;
            foreach (var kvp in miscFactories)
            {
                progressCallback(new ListGenerationProgress(progress++, miscFactories.Count, kvp.DisplayName));
                try
                {
                    otherResults = MergeResults(otherResults, kvp.GetUninstallerEntries(null).ToList(), null);
                }
                catch (Exception ex)
                {
                    PremadeDialogs.GenericError(ex);
                }
            }

            return otherResults;
        }

        /// <summary>
        /// Attach startup entries to uninstaller entries that are automatically detected as related.
        /// </summary>
        public static void AttachStartupEntries(IEnumerable<ApplicationUninstallerEntry> uninstallers, IEnumerable<StartupEntryBase> startupEntries)
        {
            // Using DoForEach to avoid multiple enumerations
            StartupManager.AssignStartupEntries(uninstallers
                .DoForEach(x => { if (x != null) x.StartupEntries = null; }), startupEntries);
        }
    }
}