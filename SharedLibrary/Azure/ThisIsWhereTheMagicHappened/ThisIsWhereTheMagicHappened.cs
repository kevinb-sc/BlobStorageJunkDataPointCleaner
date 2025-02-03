#pragma warning disable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection.Metadata;
using SharedLibrary.Models;
using static SharedLibrary.util.Util;

namespace SharedLibrary.Azure;

public partial class AzureBlobCtrl
{
    private ConcurrentDictionary<string, string> _editedFiles = new ConcurrentDictionary<string, string>();
    private string _ptJson = string.Empty;
    private ProductionDto _ptProductionDto;
    public async Task LoadPT()
    {
        _ptJson = await ReadBlobFile("pt");
        _ptProductionDto = ProductionDto.FromJson(_ptJson);
    }


    private ConcurrentDictionary<string, string> updatedDaysFiles = new();

    public async Task<bool> CheckForExistingFiles(DateTime date)
    {
        try
        {
            var alll = (await GetAllBlobsAsync());
            var blobs = alll
                        .Where(blob => blob != null)
                        .Where(blob => blob.Name.Contains($"pd{date.Year}"))
                        .ToList();

            try
            {
                var pt = alll.Where(b => b.Name.Contains("pt")).First();
                if (pt == null)
                    throw new NullReferenceException();
            }
            catch (Exception)
            {
                Console.WriteLine("No power total file found");
                return false;
            }


            if (!blobs.Any())
            {
                Log($"No File found for this date {date.ToString()}");
                //await DeleteBlobFileIfExist(GetFileName(DateOnly.FromDateTime(date), FileType.Year));
                return false;
            }
            else
            {
                if (blobs.Count == 1)
                {
                    if (blobs.FirstOrDefault().Name.Contains($"py{date.Year}"))
                    {
                        Log($"No usefull File found for this date {date.ToString()}");

                        await DeleteBlobFileIfExist(GetFileName(blobs.FirstOrDefault().Name));
                        return false;
                    }
                }
            }

            return blobs.Any();
        }
        catch (Exception e)
        {
            return false;
        }
    }

    public async Task<bool> SolvePy(DateTime date)
    {
        try
        {

            var inverters = await GetInverters();
            if (inverters == null)
            {
                Console.WriteLine("No inverters found");
                return false;
            }
            return true;
            //var alll = (await GetAllBlobsAsync());
            //var blobs = alll
            //            .Where(blob => blob.Name.Contains($"rm")
            //            || blob.Name.Contains($"rd"))
            //            .ToList();


            //if (blobs.Any())
            //{
            //    LogError("This is not a normal installation: Id : " + InstallationId);
            //    return false;
            //}
            //else
            //{
            //    if (blobs.Count == 1)
            //    {
            //        if (blobs.FirstOrDefault().Name.Contains($"py{date.Year}"))
            //        {
            //            Log($"No usefull File found for this date {date.ToString()}");

            //            await DeleteBlobFileIfExist(GetFileName(blobs.FirstOrDefault().Name));
            //            return false;
            //        }
            //    }
            //}

            //return blobs.Any();
        }
        catch (Exception e)
        {
            Console.WriteLine("Installation with custoemrs not supported");

            return false;
        }
    }
    async Task<bool> CheckForDayFiles(DateOnly date)
    {
        var blobs = await GetAllBlobsAsync();

        var yearDayBlobs = blobs.Where(x => x.Name.Contains($"pd{date.Year}")).ToList();

        if (date.Year == 2016)
            Console.WriteLine();
        if (!yearDayBlobs.Any())
        {
            var blob = blobs.FirstOrDefault();
            var res = await DeleteBlobFileIfExist(GetFileName(blob));
            return false;
        }

        return true;
    }
    public async Task<string> LetTheMagicHappen(DateOnly date)
    {

        //var duplicateResult = await DuplicateBlobFolder(ContainerName, InstallationId);
        //if (!duplicateResult)
        //{
        //    LogError("Critical Error: Cannot duplicate this installation");
        //    return null;
        //}

        bool hasfile = await CheckForDayFiles(date);
        if (!hasfile)
        {
            Log("Year" + date.Year + " Will not be handled");
            return string.Empty;
        }
        else
        {
            LogSuccess("Year" + date.Year + " Will be handled");
        }

        await DeleteAllYearFilesExceptDays(date);

        var res = await CleanYear_AllDaysFiles(date);
        if (res)
        {
            Log($"Cleaning done {date.Year}");
        }
        else
        {
            Log($"No cleaning needed {date.Year}");
        }


        await UpdatePDtoPM(date);


        Thread.Sleep(20 * 1000);
        await PMToYear(date);
        Log($"PM - > Year DONE {date.Year}");

        Thread.Sleep(20 * 1000);

        return "success";
    }

    public async Task<bool> CleanYear_AllDaysFiles(DateOnly date)
    {
        var blobs = _blobs
                    .Where(blob => blob.Name.Contains($"pd{date.Year}"))
                    .Where(blob => !blob.Name.ToLower().Contains("_backup"))
                    .ToList();


        var tasks = blobs.Select(async blob =>
        //foreach (var blob in blobs)
        {
            var fileName = GetFileName(blob.Name);
            var originalJson = await ReadBlobFile(fileName);

            if (fileName.Contains("backup") || fileName.Contains("_backup"))
                return;


            List<Inverter> updatedInverters = new();

            if (originalJson != null)
            {
                try
                {
                    var originalProduction = ProductionDto.FromJson(originalJson);

                    foreach (var inv in originalProduction.Inverters)
                    {
                        foreach (var production in inv.Production)
                        {
                            if (production == null)
                            {
                                continue;
                            }
                            else
                            {

                                if ((production.Value <= 110000) && production.Value != 0)
                                {

                                }

                                if (production.Value >= 110000)
                                {
                                    Log($"{fileName}\t" +
                                        $"Inverter: {inv.Id}\t" +
                                        $"Value: {production.Value} date {production.TimeStamp.Value.ToString()}"
                                    );

                                    production.Value = 0;
                                    production.Quality = 1;
                                }



                                if (updatedInverters.Any(x => x.Id == inv.Id))
                                {
                                    updatedInverters.First(x => x.Id == inv.Id).Production.Add(production);
                                }
                                else
                                {
                                    updatedInverters.Add(
                                        new Inverter()
                                        {
                                            Id = inv.Id,
                                            Production = new List<DataPoint> { production }
                                        });

                                }
                            }
                        }
                    }
                    var updatedProduction = new ProductionDto()
                    {
                        TimeStamp = originalProduction.TimeStamp,
                        TimeType = originalProduction.TimeType,
                        Inverters = updatedInverters,
                    };



                    var updatedJson = ProductionDto.ToJson(updatedProduction);
                    var result = await BackupAndReplaceOriginalFile(fileName, originalJson, updatedJson);
                    if (result)
                    {
                        _editedFiles.TryAdd(fileName, updatedJson); // PR: Remove to avoid memory leak
                        LogSuccess("fileName: " + fileName + " Was updated ");
                    }
                    else
                        LogError("Could Not Update filename: " + fileName);

                }
                catch (Exception e)
                {

                }
            }
        });

        Task.WaitAll(tasks.ToArray());

        if (!_editedFiles.Any())
        {
            Log("No Updated needed");
        }
        else
        {
            Log($"Removed All junks Day DataPoints {date.ToString()}", ConsoleColor.DarkBlue);
        }
        return true;
    }

    #region PD -> PM
    public async Task UpdatePDtoPM(DateOnly date)
    {
        // PD -> PM -> clouuudddd 🔥
        try
        {
            var yearDays = await GetYearDayFiles(date);

            var monthsDays = yearDays.OrderBy(x => x.Date).GroupBy(x => x.Date.Month).ToList();

            foreach (var month in monthsDays)
            {
                var monthGroup = month.ToList();
                var d = monthGroup.FirstOrDefault().Date;
                if (d.Month == 2 && d.Year == 2025)
                {
                    continue;
                }
                var result = await ProcessInverterProductionAsync(monthGroup);
            }
        }
        catch (Exception e)
        {
            LogError(e);
        }
        Log($"PD -> PM DONE {date.Year}");
    }

    public async Task<string> ProcessInverterProductionAsync(List<MonthProductionDTO> month)
    {
        var inverters = await GetInverters();

        foreach (var inverter in inverters)
        {
            var totalMonthProduction = 0.0;
            DateTime? date = null;
            List<DataPoint> productions = new List<DataPoint>();
            foreach (var day in month)
            {
                var productionDay = ProductionDto.FromJson(day.DataJson);
                var inverterProduction = productionDay.Inverters.Single(x => x.Id == inverter.Id);

                if (inverterProduction != null)
                {
                    try
                    {
                        date = new DateTime(
                                                new DateOnly(productionDay.TimeStamp.Value.Year,
                                                productionDay.TimeStamp.Value.Month,
                                                productionDay.TimeStamp.Value.Day),
                                                TimeOnly.MinValue,
                                                DateTimeKind.Utc
                                            );

                        productions.Add(new DataPoint
                        {
                            Quality = 1,
                            TimeStamp = date,
                            Value = inverterProduction.Production.Sum(x => (double)x.Value),
                        });

                        // Add total day production to the total month production
                        totalMonthProduction += inverterProduction.Production.Sum(x => (double)x.Value);
                    }
                    catch (Exception e)
                    {
                        LogError(InstallationId + " ProcessInverterProductionAsync() -> " + e);
                    }
                }
            }
            inverter.Production = productions.Distinct().ToList();
        }

        var production = new ProductionDto
        {
            TimeType = (int)FileType.Month,
            TimeStamp = inverters.First().Production.First().TimeStamp,
            Inverters = inverters.ToList(),
        };

        var dsad = month.FirstOrDefault().Date;
        try
        {
            var result = await UploadProduction(production, FileType.Month);

            return result;
        }
        catch (Exception e)
        {
            LogError("Production Month could not be uploaded ¨\t" + InstallationId + "\tDate: " + month.First().Date.ToString());
            return null;
        }
    }
    #endregion

    #region PM -> YEAR
    public async Task<string> PMToYear(DateOnly date)
    {
        try
        {
            // PM -> PY 🧸
            var yearMonthsFiles = await GetYear_MonthFilessAsync(date);

            if (yearMonthsFiles == null)
            {
                var deleted = await DeleteBlobFileIfExist(GetFileName(date, FileType.Month));
                return string.Empty;
            }

            var inverters = await GetInverters();

            var productions = new List<ProductionDto>();
            Parallel.ForEach(
                yearMonthsFiles.Where(x => x != null),
                month =>
                {
                    var prod = ProductionDto.FromJson(month.DataJson);
                    productions.Add(prod);
                }
            );
            productions = productions.OrderBy(x => x.TimeStamp).ToList();

            foreach (var inverter in inverters)
            {
                foreach (var production in productions)
                {
                    var totalProduction = production.Inverters
                                                    .Where(x => x.Id == inverter.Id)
                                                    .SelectMany(x => x.Production)
                                                    .Sum(x => (double)x.Value);
                    var updatedDate = new DateOnly(production.TimeStamp.Value.Year, production.TimeStamp.Value.Month,
                        production.TimeStamp.Value.Day);
                    inverter.Production.Add(new DataPoint
                    {
                        Quality = 1,
                        TimeStamp = new DateTime(updatedDate, TimeOnly.MinValue,
                                                    DateTimeKind.Utc),

                        Value = totalProduction,
                    });
                }

                inverter.Production = inverter.Production.OrderBy(x => x.TimeStamp).ToList();
            }

            var productionYear = new ProductionDto()
            {
                Inverters = inverters.ToList(),
                TimeType = (int)FileType.Year,
                TimeStamp = new DateTime((new DateOnly(date.Year, 1, 1)), TimeOnly.MinValue,
                                         DateTimeKind.Utc),
            };

            var jsonYearResult = await ForcePublishAndRead($"py{date.Year}", ProductionDto.ToJson(productionYear));

            return jsonYearResult;
        }
        catch (Exception e)
        {
            LogError($"YearFailed : {date.Year}" + e.Message);
        }

        return null;
    }

    private async Task<List<MonthProductionDTO>> GetYear_MonthFilessAsync(DateOnly date)
    {
        var allBlobs = await GetAllBlobsAsync();
        if (allBlobs == null || !allBlobs.Any())
            return null;


        var yearFiles = allBlobs.Where(blob => blob.Name.Contains($"pm{date.Year:D4}")).ToList();

        var tasks = new List<Task<MonthProductionDTO>>();
        var monthsFiles = new List<MonthProductionDTO>();
        foreach (var year in yearFiles)
        {
            string filename = GetFileName(year);
            var productionDate = ExtractDateFromFileName(GetFileName(filename));

            tasks.Add(ReadBlobFile(filename).ContinueWith(result =>
            {
                if (result.Result != null)
                {

                    var some = new MonthProductionDTO()
                    {
                        FileType = FileType.Year,
                        Date = productionDate,
                        DataJson = result.Result
                    };
                    return some;
                }
                return null;
            }));
        }

        var results = await Task.WhenAll(tasks);
        monthsFiles.AddRange(results);

        return monthsFiles;
    }
    #endregion

    #region Yeart -> PT
    public async Task<string> YearToPT(DateOnly date)
    {
        try
        {
            //PY -> PT -> clouuudddd 🔥
            var productions = await GetYearsAsync();
            var inverters = await GetInverters();

            var a = false;


            foreach (var inverter in inverters)
            {
                foreach (var production in productions)
                {
                    double totalProduction = 0;
                    var inv = production.Inverters.First(x => x.Id == inverter.Id);
                    double sum = (double)inv.Production.Sum(x => x.Value);
                    totalProduction += sum;

                    if (totalProduction > 0)
                        inverter.Production.Add(
                            new DataPoint()
                            {
                                Quality = 1,
                                TimeStamp =
                                new DateTime((new DateOnly(production.TimeStamp.Value.Year, 1, 1)), TimeOnly.MinValue, DateTimeKind.Utc),
                                Value = totalProduction,
                            }
                        );
                }

                inverter.Production = inverter
                                      .Production.OrderBy(x => x.TimeStamp)
                                      .ToList();
            }

            var productionTotal = new ProductionDto()
            {
                Inverters = inverters.ToList(),
                TimeType = (int)FileType.Total,
                TimeStamp = new DateTime((new DateOnly(2014, 1, 1)), TimeOnly.MinValue,
                                          DateTimeKind.Utc)
            };

            var res = string.Empty;
            var productionJson = ProductionDto.ToJson(productionTotal);
            res = await ForcePublishAndRead("pt", productionJson);

            res = await UploadProduction(productionTotal, FileType.Total);

            Log($"Year -> PT DONE {date.Year}");

            return res;
        }
        catch (Exception e)
        {
            LogError($"TotalFile failed :" + e.Message);
        }

        return null;
    }
    #endregion
    public async Task<string> UploadProduction(ProductionDto production, FileType fileType)
    {
        string fileName = string.Empty;

        string prodDay = $"{production.TimeStamp.Value.Day:D2}";
        string prodMonth = $"{production.TimeStamp.Value.Month:D2}";
        string prodYear = $"{production.TimeStamp.Value.Year}";

        switch (fileType)
        {
            case FileType.Day:
                fileName = $"pd{prodYear}{prodMonth}{prodDay}";
                break;
            case FileType.Month:
                fileName = $"pm{prodYear}{prodMonth}";
                break;
            case FileType.Year:
                fileName = $"py{prodYear}";
                break;
            case FileType.Total:
                fileName = $"pt";
                break;
        }

        var productionJson = ProductionDto.ToJson(production);
        return await ForcePublishAndRead(fileName, productionJson);
    }

    public async Task<bool> UploadProductionAsync(ProductionDto production, FileType fileType)
    {
        string fileName = string.Empty;

        string prodDay = $"{production.TimeStamp.Value.Day:D2}";
        string prodMonth = $"{production.TimeStamp.Value.Month:D2}";
        string prodYear = $"{production.TimeStamp.Value.Year}";

        switch (fileType)
        {
            case FileType.Day:
                fileName = $"pd{prodYear}{prodMonth}{prodDay}";
                break;
            case FileType.Month:
                fileName = $"pm{prodYear}{prodMonth}";
                break;
            case FileType.Year:
                fileName = $"py{prodYear}";
                break;
            case FileType.Total:
                fileName = $"pt";
                break;
        }

        var productionJson = ProductionDto.ToJson(production);
        return await ForcePublish(fileName, productionJson);
    }

    async Task<bool> DeleteAllYearFilesExceptDays(DateOnly date)
    {
        var yearBlolbBlocks = GetAllBlobsAsync().Result
                              .Where(blob => blob.Name.Contains($"py{date.Year}")
                              && blob.Name.Contains($"pm{date.Year}")
                              )
                              .ToList();

        var tasks = new List<Task>();
        foreach (var blob in yearBlolbBlocks)
        {
            tasks.Add(DeleteBlobFileIfExist(GetFileName(blob)));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception e)
        {
            LogError("Could not delete all year files, year:" + date.Year);
            LogError(e.Message);
            return false;
        }

        return true;
    }
}

// private object GetMonthFile(DateTime requestDate)
// {
//     for (int month = 1; month <= 12; month++)
//     {
//         for (int i = 0; i < requestDate.Year; i++)
//         {
//             var jsonResult = "null";
//             return jsonResult;
//         }
//     }
//
//     return false;
// }

public class MonthProductionDTO
{
    public FileType FileType { get; set; }

    public DateOnly Date { get; set; }

    public string DataJson { get; set; }
}