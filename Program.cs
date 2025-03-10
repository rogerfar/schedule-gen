using TeamGamePlanner;

var scheduler = new TeamGameScheduler();
scheduler.Start();

#if DEBUG
Console.ReadKey();
#endif