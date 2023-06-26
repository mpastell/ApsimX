using Models.Core.ApsimFile;
using Models.Core;
using Models.Climate;
using Models.Core.Run;
using APSIM.Shared.JobRunning;
using DocumentFormat.OpenXml.Validation;
using Models;
using Models.Storage;
using Models.PMF;
using DocumentFormat.OpenXml.Office2013.Drawing.ChartStyle;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClockConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //string simFile = "pytest_py.apsimx";
            string simFile = "TwinClock_test.apsimx";
            //string simFile = "WheatIClock.apsimx";
            IModel sims = FileFormat.ReadFromFile<Simulations>(simFile, e => throw e, false).NewModel;
            var sim = sims.FindChild<Models.Core.Simulation>();
            var weather = sim.FindChild<Weather>();
            //weather.FileName = "Dalby.met";
            weather.FileName = "Jokioinen.met";

            // High level Run -> Run.Runner()
            //var Runner = new Models.Core.Run.Runner(sim);
            //var e = Runner.Run();

            // Runner calls JobRunner.Run
            // From Runner.cs, Run method line 270
            //var job = new SimulationGroup(sim, true, true, false, null);
            //var jobRunner = new JobRunner(numProcessors: 1);
            //jobRunner.Add(job);
            //jobRunner.Run(true);

            // JobRunner.cs calls simulations methods, Line 188 RunActualJob
            //sim.Prepare();
            //sim.Run();
            //sim.Cleanup();

            // Try to run step by step
            var clock = (Models.TwinClock)sim.FindChild<IClock>();
            var wt = sim.FindByPath("[Wheat].Grain.Total.Wt");
            var wheat = sim.FindDescendant<Plant>();
            var leaf = wheat.FindChild<Models.PMF.Organs.Leaf>();

            var storage = sims.FindChild<DataStore>();
            storage.Dispose();
            File.Delete(storage.FileName);
            storage.Open();
            sim.Prepare();

            // Run is not called as the simulation is run step by step
            // -> New method is required to invoke events
            //sim.Commence();
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            clock.Commence(cancellationTokenSource);

            while (clock.Today <= clock.EndDate)
            {
                clock.Step();
                Console.WriteLine(clock.Today.Date.ToShortDateString() + ", " +
                    sim.Progress + "," + wt.Value + "," + leaf.LAI);
            }
            clock.Done();
            sim.Cleanup();
            storage.Close();
            Console.WriteLine("Done");
        }

    }
}