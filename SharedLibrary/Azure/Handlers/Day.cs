﻿using System;
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using SharedLibrary.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static SharedLibrary.util.Util;

namespace SharedLibrary.Azure;

public partial class AzureBlobCtrl
{
    async Task<MonthProductionDTO?> GetDayFile(DateOnly date)
    {
        return await GetDayFile(date.Month, date.Day, date);
    }

    async Task<MonthProductionDTO?> GetDayFile(string fileName)
    {
        var date = ExtractDateFromFileName(fileName);
        return await GetDayFile(date);
    }

    async Task<MonthProductionDTO?> GetDayFile(int currentMonth, int currentDay, DateOnly date)
    {
        string customTaskId = $"{currentMonth}/{currentDay}";

        string fileName = $"pd{date.Year}{currentMonth:D2}{currentDay:D2}";

        var json = await ReadBlobFile(fileName);

        if (IsValidJson(json))
        {
            try
            {
                var result = new MonthProductionDTO()
                             {
                                 DataJson = json,
                                 Date = new DateOnly(date.Year, currentMonth, currentDay),
                                 FileType = FileType.Day,
                             };
                return result;
            }
            catch (Exception ex)
            {
                LogError($"Task ID: {customTaskId} - Error Parsing Json: {fileName}");
                return null;
            }
        }
        else
        {
            LogError(fileName + "\tGetDayFile() : Invalid Json");
        }

        return null;
    }

    async Task<ConcurrentBag<MonthProductionDTO>> GetYearDayFiles(DateOnly date)
    {
        ConcurrentBag<MonthProductionDTO> dayFiles = new ConcurrentBag<MonthProductionDTO>();
        List<Task> tasks = new List<Task>();

        var allBlobs = await GetAllBlobsAsync();
        for (int month = 1; month <= 12; month++)
        {
            var filteredBlobs = allBlobs.Where(blob => blob.Name.Contains($"pd{date.Year}{month:D2}")
                                                       && !blob.Name.ToLower().Contains($"backup")).ToList();
            filteredBlobs = filteredBlobs.OrderBy(b => b.Name).ToList();

            tasks.Add(Task.Run(async () =>
            {
                var innerTasks = filteredBlobs.Select(async blob =>
                {
                    if (blob != null)
                    {
                        MonthProductionDTO? result = await GetDayFile(GetFileName(blob));
                        if (result != null)
                        {
                            dayFiles.Add(result);
                        }
                    }
                });

                await Task.WhenAll(innerTasks);
            }));
        }

        await Task.WhenAll(tasks);
        return dayFiles;
    }

    public static bool IsValidJson(string json)
    {
        if (json == "NOTFOUND" || string.IsNullOrEmpty(json))
        {
            return false;
        }
        else
        {
            try
            {
                JsonConvert.DeserializeObject<ProductionDto>(json, Converter.Settings);
                return true;
            }
            catch (Exception e)
            {
                LogError(e);
                return false;
            }
        }
    }
}