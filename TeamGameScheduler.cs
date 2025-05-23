﻿using ClosedXML.Excel;
using System.Globalization;
using System.Reflection;

namespace TeamGamePlanner;

internal class TeamGameScheduler
{
    private const Int32 BASE_SCORE = 1000;
    private const Int32 SPREAD_PENALTY_MULTIPLIER = 250;  // Applied to all distribution penalties
    private const Int32 POINTS_DOUBLEHEADER = 40;        // Base bonus for having a double header
    private const Int32 POINTS_BYE_WEEK = 20;            // Bonus for having a bye week
    
    private static readonly ThreadLocal<Random> ThreadRandom = new(() => new(Interlocked.Increment(ref _seed)));
    private static Int32 _seed = Environment.TickCount;

    private readonly Lock _lock = new();
    private readonly List<Result> _results = [];

    private Int32 _diamondCount;
    private Int32 _doubleHeadersNeeded;
    private DateTime _endDate;
    private List<DateTime> _gameDays = [];

    private Int32 _gamesPerTeam;
    private List<TimeSpan> _gameTimes = [];
    private Int32 _generateAttempts;
    private Int32 _saveTop;
    private Int32 _minScore;
    private Int32 _maxThreads;
    private String? _outputDir;
    private DateTime _startDate;
    private DateTime _timerStart;

    private Int32 _totalAttempts;
    private Int32 _totalTeams;
    private Int32 _validAttempts;
    private Int32 _highestFound = Int32.MinValue;

    public TeamGameScheduler()
    {
        ParseArgs();
    }

    private void ParseArgs()
    {
        var defaultStartDate = new DateTime(2025, 5, 13);
        var defaultEndDate = new DateTime(2025, 9, 2);
        var defaultTotalTeams = 12;
        var defaultGamesPerTeam = 22;
        var defaultDiamondCount = 4;
        var defaultGameTimes = new List<TimeSpan>
        {
            new(18, 15, 0),
            new(19, 45, 0)
        };
        var defaultGenerateAttempts = 100;
        var defaultMinScore = 2000;
        var defaultSaveTop = 10;
        var defaultMaxThreads = Environment.ProcessorCount * 2;

#if DEBUG
        _startDate = defaultStartDate;
        _endDate = defaultEndDate;
        _totalTeams = defaultTotalTeams;
        _gamesPerTeam = defaultGamesPerTeam;
        _diamondCount = defaultDiamondCount;
        _gameTimes = defaultGameTimes;
        _generateAttempts = defaultGenerateAttempts;
        _saveTop = defaultSaveTop;
        _minScore = defaultMinScore;
        _maxThreads = defaultMaxThreads;
#else
        _startDate = ReadDateFromConsole("Season start date (YYYY-MM-DD)", defaultStartDate, "yyyy-MM-dd");
        _endDate = ReadDateFromConsole("Season end date (YYYY-MM-DD)", defaultEndDate, "yyyy-MM-dd", _startDate);
        _totalTeams = ReadIntFromConsole("Number of teams", defaultTotalTeams, 2, true);
        _gamesPerTeam = ReadIntFromConsole("Games per team", defaultGamesPerTeam, 1, true);
        _diamondCount = ReadIntFromConsole("Number of diamonds", defaultDiamondCount, 1);
        _gameTimes = ReadTimeSpansFromConsole("List of game times as HH:mm, separate multiple with a comma", defaultGameTimes);
        _generateAttempts = ReadIntFromConsole("Number of schedules to generate", defaultGenerateAttempts, 1);
        _saveTop = ReadIntFromConsole("Save top schedules", defaultSaveTop, 1);
        _minScore = ReadIntFromConsole("Minimum score", defaultMinScore);
        _maxThreads = ReadIntFromConsole("Maximum threads to use", defaultMaxThreads);
#endif
    }

    public void Start()
    {
        // Validate startup parameters
        var validateParameters = ValidateParameters();

        if (!validateParameters)
        {
            return;
        }

        _gameDays = [];

        for (var date = _startDate; date <= _endDate; date = date.AddDays(7))
        {
            _gameDays.Add(date);
        }

        _doubleHeadersNeeded = (Int32) Math.Ceiling((_totalTeams - 1.0) / 2.0);

        _outputDir = WriteConfig();

        Console.WriteLine($"Writing valid schedules to {_outputDir}");
        Console.WriteLine("Press any key to start generation, press Ctrl+C to exit.");

        Console.ReadKey();

        var updateThread = CreateTimer();

        updateThread.Start();

        Parallel.For(0,
                     Int32.MaxValue,
                     new()
                     {
                         MaxDegreeOfParallelism = _maxThreads
                     },
                     (_, state) =>
                     {
                         Generate(state);
                     });

        updateThread.Join(2000);

        var totalTime = DateTime.UtcNow - _timerStart;
        Console.Clear();
        Console.WriteLine($"Generation completed in {totalTime}!");
        Console.WriteLine($"Tried {_totalAttempts:N0} total schedules to find {_validAttempts:N0} valid ones ({_validAttempts * 100.0 / _totalAttempts:F2}% valid)");
        Console.WriteLine($"Rate: {_totalAttempts / totalTime.TotalSeconds:F2} attempts/second ({_validAttempts / totalTime.TotalSeconds:F2} valid/second)");
        Console.WriteLine($"Lowest score: {_results.Min(r => r.Score)}");
        Console.WriteLine($"Highest score: {_results.Max(r => r.Score)}");
        Console.WriteLine($"Average score: {_results.Average(r => r.Score):F2}");
    }

    private Thread CreateTimer()
    {
        _timerStart = DateTime.UtcNow;

        return new(() =>
        {
            Console.Clear();

            while (_validAttempts < _generateAttempts)
            {
                var currentTime = DateTime.UtcNow;
                var elapsed = currentTime - _timerStart;
                var rate = _totalAttempts / elapsed.TotalSeconds;
                var perc = _validAttempts * 100.0 / _generateAttempts;

                var status = $"Time: {elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00} | " +
                             $"Valid: {_validAttempts:N0}/{_generateAttempts:N0} ({perc:F2}%) | " +
                             $"Rate: {rate:F2}/s | " +
                             $"Highest so far: {_highestFound} | " +
                             "Press Ctrl+C to stop";

                Console.SetCursorPosition(0, 0);
                Console.Write(new String(' ', Console.WindowWidth - 1));
                Console.SetCursorPosition(0, 0 + 1);
                Console.Write(new String(' ', Console.WindowWidth - 1));

                Console.SetCursorPosition(0, 0);
                Console.Write(status);

                Thread.Sleep(1000);
            }
        })
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
    }

    private Boolean ValidateParameters()
    {
        Console.Clear();
        Console.WriteLine("Schedule Generator");
        Console.WriteLine("=================");
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine($"- Teams: {_totalTeams}");
        Console.WriteLine($"- Games per team: {_gamesPerTeam}");
        Console.WriteLine($"- Start date: {_startDate:D}");
        Console.WriteLine($"- End date: {_endDate:D}");
        Console.WriteLine($"- Diamond count: {_diamondCount}");
        Console.WriteLine($"- Game times: {String.Join(", ", _gameTimes)}");
        Console.WriteLine();
        
        // Theoretical validations
        var gamesPerDay = _diamondCount * _gameTimes.Count; // How many games can be played per day
        var totalGameDays = (_endDate - _startDate).Days / 7 + 1; // How many game days we have
        var totalPossibleGames = gamesPerDay * totalGameDays; // Total game slots available
        var totalRequiredGames = _totalTeams * _gamesPerTeam / 2; // Total games needed (divided by 2 as each game has 2 teams)
        var roundRobinWeeks = Math.Ceiling(_totalTeams * _gamesPerTeam / (2.0 * _diamondCount * _gameTimes.Count));

        Console.WriteLine("Validation:");

        var isValid = true;

        // Check 1: Do we have enough game slots?
        if (totalPossibleGames < totalRequiredGames)
        {
            Console.WriteLine($"ERROR: Not enough game slots available!");
            Console.WriteLine($"- Total games needed: {totalRequiredGames}");
            Console.WriteLine($"- Total game slots available: {totalPossibleGames}");
            Console.WriteLine($"- Need {totalRequiredGames - totalPossibleGames} more slots");
            isValid = false;
        }

        // Check 2: Is round robin theoretically possible?
        if (roundRobinWeeks > totalGameDays)
        {
            Console.WriteLine($"ERROR: Not enough weeks for round robin!");
            Console.WriteLine($"- Weeks needed: {roundRobinWeeks}");
            Console.WriteLine($"- Weeks available: {totalGameDays}");
            Console.WriteLine($"- Need {roundRobinWeeks - totalGameDays} more weeks");
            isValid = false;
        }

        // Check 3: Is the number of games per team even?
        if (_gamesPerTeam % 2 != 0)
        {
            Console.WriteLine($"ERROR: Games per team must be even!");
            Console.WriteLine($"- Current games per team: {_gamesPerTeam}");
            isValid = false;
        }

        // Check 4: Is total games mathematically possible?
        if (_totalTeams * _gamesPerTeam % 2 != 0)
        {
            Console.WriteLine($"ERROR: Total games (teams × games per team) must be even!");
            Console.WriteLine($"- Current total: {_totalTeams * _gamesPerTeam}");
            isValid = false;
        }

        Console.WriteLine();

        if (!isValid)
        {
            Console.WriteLine("Schedule generation cannot proceed due to validation errors.");

            return false;
        }

        Console.WriteLine("Statistics:");
        Console.WriteLine($"- Total game days: {totalGameDays}");
        Console.WriteLine($"- Games per day possible: {gamesPerDay}");
        Console.WriteLine($"- Total game slots available: {totalPossibleGames}");
        Console.WriteLine($"- Total games needed: {totalRequiredGames}");
        Console.WriteLine($"- Required round robin weeks: {roundRobinWeeks}");

        return true;
    }

    private String WriteConfig()
    {
        // Create the output directory
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var assemlbyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var schedulesDir = Path.Combine(assemlbyPath, "schedules");
#if DEBUG
        if (Directory.Exists(schedulesDir))
        {
            Directory.Delete(schedulesDir, true);
        }
#endif
        if (!Directory.Exists(schedulesDir))
        {
            Directory.CreateDirectory(schedulesDir);
        }

        var outputDir = Path.Combine(schedulesDir, timestamp);
        Directory.CreateDirectory(outputDir);

        using var writer = new StreamWriter(Path.Combine(outputDir, "config.txt"));

        writer.WriteLine($"Teams: {_totalTeams}");
        writer.WriteLine($"Games per team: {_gamesPerTeam}");
        writer.WriteLine($"Start date: {_startDate:D}");
        writer.WriteLine($"End date: {_endDate:D}");
        writer.WriteLine($"Diamond count: {_diamondCount}");
        writer.WriteLine($"Game times: {String.Join(", ", _gameTimes)}");
        writer.WriteLine($"Valid schedules to generate: {_generateAttempts:N0}");
        writer.WriteLine($"Maximum threads: {_maxThreads}");
        writer.WriteLine($"Max double headers: {_doubleHeadersNeeded}");

        return outputDir;
    }

    private void Generate(ParallelLoopState state)
    {
        if (_validAttempts >= _generateAttempts)
        {
            state.Stop();

            return;
        }

        var schedule = GenerateRandomSchedule();
        Interlocked.Increment(ref _totalAttempts);

        if (schedule == null)
        {
            return;
        }

        if (!ValidateSchedule(schedule))
        {
            return;
        }

        if (!ValidateDoubleHeaders(schedule))
        {
            return;
        }

        if (!ValidateSingleGames(schedule, 2))
        {
            return;
        }
        
        var score = ScoreSchedule(schedule);
        
        lock (_lock)
        {
            if (score > _highestFound && score >= _minScore)
            {
                _highestFound = score;

                SaveScheduleWithStats(schedule, score);
            }

            _results.Add(new()
            {
                Schedule = schedule,
                Score = score
            });

            SaveScheduleWithStats(schedule, score);

            while (_results.Count > _saveTop)
            {
                var item = _results.OrderByDescending(m => m.Score).First();
                _results.Remove(item);
            }
        }

        Interlocked.Increment(ref _validAttempts);
    }

    private List<Game>? GenerateRandomSchedule()
    {
        var matchups = GenerateMatchups();
        var schedule = new List<Game>();
        var random = ThreadRandom.Value!;

        var attempt = 0;

        while (attempt < 1000000)
        {
            schedule.Clear();
            var remainingMatchups = new List<Game>(matchups.OrderBy(_ => random.Next()));

            foreach (var date in _gameDays)
            foreach (var time in _gameTimes)
            {
                var teamsInThisTimeslot = new HashSet<Int32>();
                var gamesInThisTimeslot = 0;

                while (gamesInThisTimeslot < _diamondCount && remainingMatchups.Any())
                {
                    // Only consider matchups where neither team is already playing in this timeslot
                    var availableMatchups = remainingMatchups
                                            .Where(m => !teamsInThisTimeslot.Contains(m.HomeTeam) && 
                                                        !teamsInThisTimeslot.Contains(m.AwayTeam))
                                            .ToList();

                    if (!availableMatchups.Any())
                    {
                        break;
                    }

                    var matchup = availableMatchups[random.Next(availableMatchups.Count)];

                    schedule.Add(new()
                    {
                        GameDate = date,
                        GameTime = time,
                        GameNumber = gamesInThisTimeslot + 1,
                        HomeTeam = matchup.HomeTeam,
                        AwayTeam = matchup.AwayTeam,
                        Diamond = gamesInThisTimeslot + 1
                    });

                    teamsInThisTimeslot.Add(matchup.HomeTeam);
                    teamsInThisTimeslot.Add(matchup.AwayTeam);
                    remainingMatchups.Remove(matchup);
                    gamesInThisTimeslot++;
                }
            }

            if (!remainingMatchups.Any())
            {
                return schedule;
            }

            attempt++;
        }

        return null;
    }

    private List<Game> GenerateMatchups()
    {
        var matchups = new List<Game>();

        for (var i = 1; i <= _totalTeams; i++)
        {
            for (var j = i + 1; j <= _totalTeams; j++)
            {
                matchups.Add(new()
                {
                    HomeTeam = i,
                    AwayTeam = j
                });

                matchups.Add(new()
                {
                    HomeTeam = j,
                    AwayTeam = i
                });
            }
        }

        return matchups;
    }

    private Boolean ValidateSchedule(List<Game> schedule)
    {
        var teamGameCount = new Dictionary<Int32, Int32>();

        // Initialize count for each team
        for (var i = 1; i <= _totalTeams; i++)
        {
            teamGameCount[i] = 0;
        }

        // Count games for each team
        foreach (var game in schedule)
        {
            teamGameCount[game.HomeTeam]++;
            teamGameCount[game.AwayTeam]++;
        }

        // Validate total games scheduled
        var totalGamesNeeded = _totalTeams * _gamesPerTeam / 2;

        if (schedule.Count != totalGamesNeeded)
        {
            return false;
        }

        // Ensure all teams have exactly required games
        foreach (var (_, count) in teamGameCount)
        {
            if (count != _gamesPerTeam)
            {
                return false;
            }
        }

        // Check that no team plays twice in the same timeslot
        var gamesByDateAndTime = schedule.GroupBy(g => new
        {
            Date = g.GameDate,
            Time = g.GameTime
        });

        foreach (var timeSlot in gamesByDateAndTime)
        {
            var teamsInTimeslot = new HashSet<Int32>();

            foreach (var game in timeSlot)
            {
                // Check home team
                if (!teamsInTimeslot.Add(game.HomeTeam))
                {
                    return false;
                }

                // Check away team
                if (!teamsInTimeslot.Add(game.AwayTeam))
                {
                    return false;
                }
            }

            // Verify we don't exceed maximum teams per timeslot
            if (teamsInTimeslot.Count > _diamondCount * 2)
            {
                return false;
            }
        }

        return true;
    }

    private Boolean ValidateDoubleHeaders(List<Game> schedule)
    {
        var teamDoubleHeaders = new Dictionary<Int32, Int32>();

        // Initialize all teams
        for (var i = 1; i <= _totalTeams; i++)
        {
            teamDoubleHeaders[i] = 0;
        }

        // Group games by date
        var gamesByDate = schedule
                          .GroupBy(g => g.GameDate)
                          .ToDictionary(g => g.Key, g => g.ToList());

        // Count doubleheaders for each team
        foreach (var dateGames in gamesByDate.Values)
        {
            var teamsGamesForDay = new Dictionary<Int32, Int32>();

            foreach (var game in dateGames)
            {
                teamsGamesForDay.TryAdd(game.HomeTeam, 0);

                teamsGamesForDay.TryAdd(game.AwayTeam, 0);

                teamsGamesForDay[game.HomeTeam]++;
                teamsGamesForDay[game.AwayTeam]++;
            }

            foreach (var (team, gamesPlayed) in teamsGamesForDay)
            {
                if (gamesPlayed == 2)
                {
                    teamDoubleHeaders[team]++;
                }
            }
        }

        return teamDoubleHeaders.All(kvp => kvp.Value >= _doubleHeadersNeeded);
    }

    private Boolean ValidateSingleGames(List<Game> schedule, Int32 maxConsecutiveSingles)
    {
        var teamGames = new Dictionary<Int32, List<(DateTime date, Int32 gameCount)>>();

        // First, organize all games by team and date, counting games per date
        for (var team = 1; team <= _totalTeams; team++)
        {
            var gamesForTeam = schedule
                .Where(g => g.HomeTeam == team || g.AwayTeam == team)
                .GroupBy(g => g.GameDate)
                .Select(g => (date: g.Key, gameCount: g.Count()))
                .OrderBy(g => g.date)
                .ToList();

            teamGames[team] = gamesForTeam;
        }

        // Now check each team's game pattern
        foreach (var (team, games) in teamGames)
        {
            var consecutiveSingles = 0;
            var foundFirstDoubleHeader = false;

            for (var i = 0; i < games.Count; i++)
            {
                if (games[i].gameCount == 2)
                {
                    // Found a double header
                    foundFirstDoubleHeader = true;
                    consecutiveSingles = 0;
                }
                else if (games[i].gameCount == 1)
                {
                    if (foundFirstDoubleHeader)
                    {
                        consecutiveSingles++;
                        
                        // Check if we've exceeded our limit of consecutive singles
                        if (consecutiveSingles > maxConsecutiveSingles)
                        {
                            // Look ahead to see if there's another double header
                            var hasAnotherDoubleHeader = false;
                            for (var j = i + 1; j < games.Count; j++)
                            {
                                if (games[j].gameCount == 2)
                                {
                                    hasAnotherDoubleHeader = true;
                                    break;
                                }
                            }

                            // Only fail validation if this stretch of singles is between two double headers
                            if (hasAnotherDoubleHeader)
                            {
                                return false; // Found more than maxConsecutiveSingles consecutive singles between double headers
                            }
                        }
                    }
                }
            }
        }

        return true; // All teams pass validation
    }

    public Int32 ScoreSchedule(List<Game> schedule)
    {
        var score = BASE_SCORE;

        // Track statistics per team
        var teamDoubleHeaders = new Dictionary<Int32, Int32>();
        var teamSingleGameStats = new Dictionary<Int32, (Int32 Early, Int32 Late)>();
        var teamConsecutiveSingles = new Dictionary<Int32, Dictionary<DateTime, Int32>>();

        // Initialize tracking dictionaries
        for (var i = 1; i <= _totalTeams; i++)
        {
            teamDoubleHeaders[i] = 0;
            teamSingleGameStats[i] = (0, 0);
            teamConsecutiveSingles[i] = new();
        }

        // Get all possible game dates (Tuesdays)
        var allPossibleDates = Enumerable.Range(0, (_endDate - _startDate).Days + 1)
                                       .Select(days => _startDate.AddDays(days))
                                       .Where(date => date.DayOfWeek == DayOfWeek.Tuesday)
                                       .OrderBy(d => d)
                                       .ToList();

        // Process games date by date
        foreach (var date in allPossibleDates)
        {
            var gamesOnDate = schedule.Where(g => g.GameDate == date).ToList();
            var teamsGamesForDay = new Dictionary<Int32, List<Game>>();

            // Group games by team
            foreach (var game in gamesOnDate)
            {
                if (!teamsGamesForDay.ContainsKey(game.HomeTeam))
                {
                    teamsGamesForDay[game.HomeTeam] = [];
                }

                if (!teamsGamesForDay.ContainsKey(game.AwayTeam))
                {
                    teamsGamesForDay[game.AwayTeam] = [];
                }

                teamsGamesForDay[game.HomeTeam].Add(game);
                teamsGamesForDay[game.AwayTeam].Add(game);
            }

            // Process each team's games for the day
            for (var team = 1; team <= _totalTeams; team++)
            {
                if (!teamsGamesForDay.TryGetValue(team, out var games))
                {
                    // Team has a bye
                    teamConsecutiveSingles[team][date] = 0;
                    score += POINTS_BYE_WEEK;
                    continue;
                }

                if (games.Count == 1) // Single game
                {
                    // Track consecutive singles
                    var previousDate = allPossibleDates
                        .Where(d => d < date)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();

                    var previousCount = previousDate != default 
                        ? teamConsecutiveSingles[team].GetValueOrDefault(previousDate, 0) 
                        : 0;

                    teamConsecutiveSingles[team][date] = previousCount + 1;

                    // Track early/late distribution
                    var stats = teamSingleGameStats[team];
                    if (games[0].GameTime == _gameTimes[0]) // Early game
                    {
                        teamSingleGameStats[team] = (stats.Early + 1, stats.Late);
                    }
                    else // Late game
                    {
                        teamSingleGameStats[team] = (stats.Early, stats.Late + 1);
                    }
                }
                else if (games.Count == 2) // Double header
                {
                    teamConsecutiveSingles[team][date] = 0;
                    teamDoubleHeaders[team]++;
                    score += POINTS_DOUBLEHEADER;
                }
            }
        }

        // Score double header distribution
        var minDoubleHeaders = teamDoubleHeaders.Values.Min();
        var maxDoubleHeaders = teamDoubleHeaders.Values.Max();
        var avgDoubleHeaders = teamDoubleHeaders.Values.Average();
        
        var dhVariance = teamDoubleHeaders.Values
            .Select(dh => Math.Pow(dh - avgDoubleHeaders, 2))
            .Average();
        var dhStandardDeviation = Math.Sqrt(dhVariance);

        var dhSpreadPenalty = (maxDoubleHeaders - minDoubleHeaders) * SPREAD_PENALTY_MULTIPLIER;
        var dhDeviationPenalty = (Int32)(dhStandardDeviation * SPREAD_PENALTY_MULTIPLIER);
        
        score -= dhSpreadPenalty;
        score -= dhDeviationPenalty;

        // Score consecutive singles distribution
        var maxConsecutiveSingles = new Dictionary<Int32, Int32>();
        for (var team = 1; team <= _totalTeams; team++)
        {
            maxConsecutiveSingles[team] = teamConsecutiveSingles[team].Values.Max();
        }

        var minConsecutive = maxConsecutiveSingles.Values.Min();
        var maxConsecutive = maxConsecutiveSingles.Values.Max();
        var avgConsecutive = maxConsecutiveSingles.Values.Average();

        var csVariance = maxConsecutiveSingles.Values
            .Select(cs => Math.Pow(cs - avgConsecutive, 2))
            .Average();
        var csStandardDeviation = Math.Sqrt(csVariance);

        var csSpreadPenalty = (maxConsecutive - minConsecutive) * SPREAD_PENALTY_MULTIPLIER;
        var csDeviationPenalty = (Int32)(csStandardDeviation * SPREAD_PENALTY_MULTIPLIER);

        score -= csSpreadPenalty;
        score -= csDeviationPenalty;

        // Score early vs late game distribution
        var earlyLateImbalances = new Dictionary<Int32, Int32>();

        foreach (var team in teamSingleGameStats.Keys)
        {
            var (early, late) = teamSingleGameStats[team];
            var totalSingles = early + late;
            
            // Calculate imbalance (how far from 50/50 split)
            var idealEarly = totalSingles / 2.0;
            var imbalance = Math.Abs(early - idealEarly);
            earlyLateImbalances[team] = (Int32)Math.Round(imbalance);
        }

        var minImbalance = earlyLateImbalances.Values.Min();
        var maxImbalance = earlyLateImbalances.Values.Max();
        var avgImbalance = earlyLateImbalances.Values.Average();

        var elVariance = earlyLateImbalances.Values
            .Select(imb => Math.Pow(imb - avgImbalance, 2))
            .Average();
        var elStandardDeviation = Math.Sqrt(elVariance);

        var elSpreadPenalty = (maxImbalance - minImbalance) * SPREAD_PENALTY_MULTIPLIER;
        var elDeviationPenalty = (Int32)(elStandardDeviation * SPREAD_PENALTY_MULTIPLIER);

        score -= elSpreadPenalty;
        score -= elDeviationPenalty;

        return score;
    }

    private void SaveScheduleWithStats(List<Game> schedule, Int32 score)
    {
        var filename = $"{score:D4}.xlsx";
        var workbook = new XLWorkbook();

        // Worksheet 1: General Stats
        var generalStats = workbook.AddWorksheet("General Stats");
        
        // Configuration section
        generalStats.Cell("A1").Value = "Configuration";
        generalStats.Cell("A3").Value = "Teams";
        generalStats.Cell("B3").Value = _totalTeams;
        generalStats.Cell("A4").Value = "Games per team";
        generalStats.Cell("B4").Value = _gamesPerTeam;
        generalStats.Cell("A5").Value = "Start date";
        generalStats.Cell("B5").Value = _startDate;
        generalStats.Cell("A6").Value = "End date";
        generalStats.Cell("B6").Value = _endDate;
        generalStats.Cell("A7").Value = "Diamond count";
        generalStats.Cell("B7").Value = _diamondCount;
        generalStats.Cell("A8").Value = "Game times";
        generalStats.Cell("B8").Value = String.Join(", ", _gameTimes);
        generalStats.Cell("A9").Value = "Double headers needed";
        generalStats.Cell("B9").Value = _doubleHeadersNeeded;

        // Team Statistics
        generalStats.Cell("A11").Value = "Team Statistics";
        var headerRow = generalStats.Row(12);
        headerRow.Cell(1).Value = "Team";
        headerRow.Cell(2).Value = "Home Games";
        headerRow.Cell(3).Value = "Away Games";
        headerRow.Cell(4).Value = "Total Games";
        headerRow.Cell(5).Value = "Early Games";
        headerRow.Cell(6).Value = "Late Games";
        headerRow.Cell(7).Value = "Early %";
        headerRow.Cell(8).Value = "Double Headers";

        var row = 13;
        for (var team = 1; team <= _totalTeams; team++)
        {
            var homeGames = schedule.Count(g => g.HomeTeam == team);
            var awayGames = schedule.Count(g => g.AwayTeam == team);
            var totalGames = homeGames + awayGames;
            var earlyGames = schedule.Count(g => (g.HomeTeam == team || g.AwayTeam == team) && g.GameTime == _gameTimes[0]);
            var lateGames = schedule.Count(g => (g.HomeTeam == team || g.AwayTeam == team) && g.GameTime == _gameTimes[1]);
            var earlyPercent = earlyGames * 100.0 / totalGames;
            
            var doubleHeaders = schedule
                .Where(g => g.HomeTeam == team || g.AwayTeam == team)
                .GroupBy(g => g.GameDate)
                .Count(dayGames => dayGames.Select(g => g.GameTime).Distinct().Count() == 2);

            var dataRow = generalStats.Row(row);
            dataRow.Cell(1).Value = $"Team {team}";
            dataRow.Cell(2).Value = homeGames;
            dataRow.Cell(3).Value = awayGames;
            dataRow.Cell(4).Value = totalGames;
            dataRow.Cell(5).Value = earlyGames;
            dataRow.Cell(6).Value = lateGames;
            dataRow.Cell(7).Value = $"{earlyPercent:F2}%";
            dataRow.Cell(8).Value = doubleHeaders;
            row++;
        }

        // Worksheet 2: Full Schedule
        var fullSchedule = workbook.AddWorksheet("Schedule");
        fullSchedule.Cell("A1").Value = "Date";
        fullSchedule.Cell("B1").Value = "Time";
        fullSchedule.Cell("C1").Value = "Diamond";
        fullSchedule.Cell("D1").Value = "Home";
        fullSchedule.Cell("E1").Value = "Away";

        row = 2;
        foreach (var game in schedule.OrderBy(g => g.GameDate).ThenBy(g => g.GameTime).ThenBy(g => g.Diamond))
        {
            var dataRow = fullSchedule.Row(row);
            dataRow.Cell(1).Value = game.GameDate;
            dataRow.Cell(2).Value = game.GameTime;
            dataRow.Cell(3).Value = game.Diamond;
            dataRow.Cell(4).Value = $"Team {game.HomeTeam}";
            dataRow.Cell(5).Value = $"Team {game.AwayTeam}";
            row++;
        }

        // Worksheets 3-15: Individual Team Schedules
        for (var team = 1; team <= _totalTeams; team++)
        {
            var teamSchedule = workbook.AddWorksheet($"Team {team}");
            teamSchedule.Cell("A1").Value = "Date";
            teamSchedule.Cell("B1").Value = "Time";
            teamSchedule.Cell("C1").Value = "Diamond";
            teamSchedule.Cell("D1").Value = "Home/Away";
            teamSchedule.Cell("E1").Value = "Opponent";

            var teamGames = schedule
                .Where(g => g.HomeTeam == team || g.AwayTeam == team)
                .OrderBy(g => g.GameDate)
                .ThenBy(g => g.GameTime);

            row = 2;
            foreach (var game in teamGames)
            {
                var isHome = game.HomeTeam == team;
                var dataRow = teamSchedule.Row(row);
                dataRow.Cell(1).Value = game.GameDate;
                dataRow.Cell(2).Value = game.GameTime;
                dataRow.Cell(3).Value = game.Diamond;
                dataRow.Cell(4).Value = isHome ? "Home" : "Away";
                dataRow.Cell(5).Value = $"Team {(isHome ? game.AwayTeam : game.HomeTeam)}";
                row++;
            }

            // Highlight double headers
            var doubleHeaderDates = teamGames
                .GroupBy(g => g.GameDate)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var date in doubleHeaderDates)
            {
                var dateRows = Enumerable.Range(2, row - 2)
                    .Where(r => teamSchedule.Cell(r, 1).GetValue<DateTime>() == date)
                    .ToList();

                foreach (var r in dateRows)
                {
                    teamSchedule.Range(r, 1, r, 5).Style.Fill.BackgroundColor = XLColor.LightYellow;
                }
            }
        }

        // Auto-fit columns in all worksheets
        foreach (var worksheet in workbook.Worksheets)
        {
            worksheet.Columns().AdjustToContents();
        }

        workbook.SaveAs(Path.Combine(_outputDir!, filename));
    }

    private static Int32 ReadIntFromConsole(String prompt, Int32 defaultValue, Int32 min = 0, Boolean requireEven = false)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var input = Console.ReadLine();

            // Use default if empty
            if (String.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            // Parse and validate
            if (Int32.TryParse(input, out var value))
            {
                if (value < min)
                {
                    Console.WriteLine($"Value must be at least {min}. Please try again.");

                    continue;
                }

                if (requireEven && value % 2 != 0)
                {
                    Console.WriteLine("Value must be even. Please try again.");

                    continue;
                }

                return value;
            }

            Console.WriteLine("Invalid input. Please enter a number.");
        }
    }

    private static DateTime ReadDateFromConsole(String prompt, DateTime defaultValue, String format, DateTime? minDate = null)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue.ToString(format)}]: ");
            var input = Console.ReadLine();

            // Use default if empty
            if (String.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            // Parse and validate
            if (DateTime.TryParseExact(input, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                if (minDate.HasValue && date < minDate.Value)
                {
                    Console.WriteLine($"Date must be on or after {minDate.Value.ToString(format)}. Please try again.");

                    continue;
                }

                return date;
            }

            Console.WriteLine($"Invalid date format. Please use {format}.");
        }
    }

    private static List<TimeSpan> ReadTimeSpansFromConsole(String prompt, List<TimeSpan> defaultValue)
    {
        while (true)
        {
            Console.Write($"{prompt} [{String.Join(", ", defaultValue.Select(m => m.ToString(@"hh\:mm")))}]: ");
            var input = Console.ReadLine();

            // Use default if empty
            if (String.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            // Parse and validate
            var times = input.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim())
                             .ToList();

            if (times.Count > 0 && times.All(t => TimeSpan.TryParse(t, out _)))
            {
                return times.Select(TimeSpan.Parse).ToList();
            }

            Console.WriteLine("Invalid time format. Please use HH:mm.");
        }
    }
}

internal class Game
{
    public DateTime GameDate { get; set; }
    public Int32 GameNumber { get; set; }
    public TimeSpan GameTime { get; set; }
    public Int32 HomeTeam { get; set; }
    public Int32 AwayTeam { get; set; }
    public Int32 Diamond { get; set; }
}

internal class Result
{
    public Int32 Score { get; set; }
    public List<Game> Schedule { get; set; }
}