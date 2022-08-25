﻿using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump
{
    [Command]
    [Subcommand(typeof(PumpTestDataCommand))]
    [Subcommand(typeof(PumpAllScores))]
    [Subcommand(typeof(WatchNewScores))]
    [Subcommand(typeof(ClearQueue))]
    [Subcommand(typeof(ImportHighScores))]
    [Subcommand(typeof(AddMaximumStatisticsToLazerScores))]
    public class Program
    {
        public static void Main(string[] args)
        {
            CommandLineApplication.Execute<Program>(args);
        }

        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp();
            return 1;
        }
    }
}
