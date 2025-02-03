﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Text;
using static SharedLibrary.ApplicationVariables;
using static Microsoft.WindowsAzure.Storage.CloudStorageAccount;
using SharedLibrary.util;
using static SharedLibrary.util.Util;
using System.Numerics;

namespace SharedLibrary.Azure
{
    public partial class AzureBlobCtrl
    {
        #region Create

        public async Task<bool> CreateAndUploadBlobFile(string jsonContent, string fileName,
                                                        string containerName = "installations", string source = "")
        {
            string zipFileName = $"{fileName}.zip";
            CloudBlobContainer container = GetContainerReference(containerName);
            CloudBlockBlob blobFile = container.GetBlockBlobReference($"{InstallationId}/{zipFileName}");

            using (MemoryStream compressedStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(compressedStream, ZipArchiveMode.Create, true))
                {
                    var jsonFileEntry = archive.CreateEntry($"{fileName}.json");

                    using (var entryStream = jsonFileEntry.Open())
                    using (var streamWriter = new StreamWriter(entryStream))
                    {
                        await streamWriter.WriteAsync(jsonContent);
                    }
                }

                compressedStream.Seek(0, SeekOrigin.Begin);
                await blobFile.UploadFromStreamAsync(compressedStream);
            }

            if (source != "PUBLISH")
            {
                Log($"{fileName} - {InstallationId} production object was created", ConsoleColor.Yellow);
            }

            return true;
        }

        #endregion

        #region Read

        public async Task<string> ReadBlobFile(string fileName)
        {
            var json = await ReadBlobFile($"{fileName}.json", $"{fileName}.zip", InstallationId);

            if (!IsValidJson(json)) // PR: Can be removed
            {
                LogError($"READ: Invalid Json- {fileName} response {json}");
                return null;
            }
            return json;
        }

        public async Task<string> ReadBlobFile(string jsonFileName, string zipFileName, string? sn)
        {
            if (string.IsNullOrEmpty(sn) && !string.IsNullOrEmpty(InstallationId))
                sn = InstallationId;
            else if (string.IsNullOrEmpty(sn) && string.IsNullOrEmpty(InstallationId))
                LogError("Either 'InstallationId' or'sn' parameter must be provided.");

            if (!zipFileName.EndsWith(".zip"))
                LogError("file name must end with .zip");
            if (!jsonFileName.EndsWith(".json"))
                LogError("file name must end with .json");

            string json = "null";

            CloudBlockBlob blobFile = await GetBlockBlobReference(zipFileName);
            if (blobFile != null)
            {
                using (var zipStream = new MemoryStream())
                {
                    await blobFile.DownloadToStreamAsync(zipStream);
                    zipStream.Position = 0;

                    using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                    {
                        ZipArchiveEntry? zipArchiveEntry = archive.GetEntry(jsonFileName);
                        if (zipArchiveEntry != null)
                        {
                            using (StreamReader sr = new StreamReader(zipArchiveEntry.Open()))
                            {
                                json = await sr.ReadToEndAsync();
                            }
                        }
                        else
                        {
                            LogError("ReadBlobFile() -> zip entry was null");
                        }
                    }
                }

                return json;

            }
            return null;
        }

        #endregion

        #region update

        public async Task<bool> WriteJson(string json, string fileName, string? sn = null)
        {
            sn = ValidateFileName(fileName, sn);

            CloudBlockBlob blobFile = await GetBlockBlobReference($"{fileName}.zip");

            using (MemoryStream ms = new MemoryStream(UTF8Encoding.UTF8.GetBytes(json)))
            {
                using (MemoryStream compressed = new MemoryStream())
                {
                    using (var archive = new ZipArchive(compressed, ZipArchiveMode.Create, true))
                    {
                        var jsonFile = archive.CreateEntry($"{fileName}.json");

                        using (var entryStream = jsonFile.Open())
                        using (var streamWriter = new StreamWriter(entryStream))
                        {
                            await streamWriter.WriteAsync(json);
                        }
                    }

                    compressed.Seek(0, SeekOrigin.Begin);
                    compressed.Position = 0;
                    await blobFile.UploadFromStreamAsync(compressed);
                }
            }

            return true;
        }

        #endregion

        #region Delete

        public async Task<bool> DeleteBlobFileIfExist(string zip, string containerName = "installations")
        {
            if (!zip.EndsWith(".zip"))
                zip = zip.Trim() + ".zip";

            CloudBlobContainer container = GetContainerReference(containerName);
            var blobFile = container.GetBlockBlobReference($"{InstallationId}/{zip}");
            var result = await blobFile.DeleteIfExistsAsync();
            return result;
        }
        public async Task<bool> DeleteBlobFile(string zip, string containerName = "installations")
        {
            if (!zip.EndsWith(".zip"))
                zip = zip.Trim() + ".zip";

            try
            {
                CloudBlobContainer container = GetContainerReference(containerName);
                var blobFile = container.GetBlockBlobReference($"{InstallationId}/{zip}");
                var result = await blobFile.DeleteIfExistsAsync();

                if (result == false)
                {
                    LogError("Could not delete file: " + zip);
                }

                return result;
            }
            catch (Exception ex)
            {
                LogError("Could not delete zip " + zip);
                LogError(ex.ToString());
                return false;
            }
        }

        #endregion
        #region Duplicate 

        private async Task<bool> DuplicateBlobFolder(string containerName, string folderName)
        {
            try
            {
                CloudBlobContainer container = GetContainerReference(containerName);
                if (!await container.ExistsAsync())
                {
                    LogError($"Container '{containerName}' does not exist.");
                    return false;
                }

                // Generate the backup folder name
                string backupFolderName = $"{folderName}_Backup_{DateTime.Now:yyyyMMdd}";

                // List all blobs in the folder
                BlobContinuationToken continuationToken = null;
                var blobsToCopy = new List<IListBlobItem>();

                do
                {
                    var resultSegment = await container.ListBlobsSegmentedAsync(folderName + "/", true, BlobListingDetails.None, null, continuationToken, null, null);
                    continuationToken = resultSegment.ContinuationToken;
                    blobsToCopy.AddRange(resultSegment.Results);
                } while (continuationToken != null);

                // Copy each blob to the backup folder
                foreach (var blobItem in blobsToCopy)
                {
                    if (blobItem is CloudBlockBlob sourceBlob)
                    {
                        // Generate the destination blob name
                        string relativePath = sourceBlob.Name.Substring(folderName.Length + 1); // Remove the folder prefix
                        string destinationBlobName = $"{DateOnly.FromDateTime(DateTime.Now)}_BackUp/{folderName}/{relativePath}";

                        // Get a reference to the destination blob
                        CloudBlockBlob destinationBlob = container.GetBlockBlobReference(destinationBlobName);

                        // Start the copy operation
                        await destinationBlob.StartCopyAsync(sourceBlob.Uri);
                    }
                }

                Log($"Folder '{folderName}' successfully duplicated to '{backupFolderName}'", ConsoleColor.Green);
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Error duplicating folder '{folderName}': {ex.Message}");
                return false;
            }
        }
        #endregion
    }
}