using Models.Core.ApsimFile;
using Models.Core;
using Models.Climate;
using Models.Core.Run;
using APSIM.Shared.JobRunning;
using Models;
using Models.Storage;
using Models.PMF;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using Models.TwinYields;
using System;
using Models.AgPasture;


namespace ClockConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //string simFile = "pytest_py.apsimx";
            //string simFile = "TwinClock_test.apsimx";
            //string simFile = "AGPRyeGrassDates.apsimx";
            //string simFile = "WheatIClock.apsimx";
            //string simFile = "WheatProtoTwinClock.apsimx";
            //string simFile = "../../RVIII_2022/model/WheatProto.apsimx";
            string simFile = "../../grassmodels/models/AGPRyeGrassDates.apsimx";
            IModel sims = FileFormat.ReadFromFile<Simulations>(simFile, e => throw e, false).NewModel;

            var weather = sims.FindDescendant<Weather>();
            //weather.FileName = "Jokioinen.met";


            //var ops = sim.FindDescendant<Models.Operations>();
            //var op = ops.Operation.First();
            // High level Run -> Run.Runner()
            //var Runner = new Models.Core.Run.Runner(sim);
            //var e = Runner.Run();

            // Runner calls JobRunner.Run
            // From Runner.cs, Run method line 270
            //var job = new SimulationGroup(sim, true, true, false, null);
            //var jobRunner = new JobRunner(numProcessors: 1);
            //jobRunner.Add(job);
            //jobRunner.Run(true);
/*
            SimulationEnsemble sn = new SimulationEnsemble(sims, 10);
            sn.Prepare();
            sn.Commence();
            var wht = sn.Simulations[0].FindDescendant<Plant>();

            while (sn.Today <= sn.EndDate)
            {
                sn.Step();
                Console.WriteLine(sn.Today.Date.ToShortDateString() + "," +
                    wht.LAI.ToString()
                    );
            }
            sn.Done(); */


            ModelEnsemble en = new ModelEnsemble(sims, 4, 4);
            en.Prepare();
            en.Commence();

            //var wht = en.Models[0].FindDescendant<Plant>();
            var agp = en.Models[0].FindDescendant<PastureSpecies>();

            while (en.Today <= en.EndDate)
            {
                en.Step();
                Console.WriteLine(en.Today.Date.ToShortDateString() + "," + agp.AboveGroundHarvestable.Wt.ToString()
                    //wht.LAI.ToString()
                    );
            }
            en.Done();

            // JobRunner.cs calls simulations methods, Line 188 RunActualJob
            //sim.Prepare();
            //sim.Run();
            //sim.Cleanup();

            // Try to run step by step
            var sim = sims.FindChild<Models.Core.Simulation>();
            weather = sim.FindChild<Weather>();

            var clock = (Models.TwinClock)sim.FindChild<IClock>();
            var wt = sim.FindByPath("[Wheat].Grain.Total.Wt");
            var wheat = sim.FindDescendant<Plant>();
             var leaf = wheat.FindChild<Models.PMF.Organs.Leaf>();

            var storage = sims.FindChild<DataStore>();
            storage.Enabled = false;
            sims.Children.Remove(sims.FindChild<Report>());


            storage.Dispose();
            File.Delete(storage.FileName);
            storage.Open();
            sim.Prepare();

            // Run is not called as the simulation is run step by step
            // -> New method is required to invoke events
            //sim.Commence();
            CancellationTokenSource cts = new CancellationTokenSource();
            clock.Commence(cts);

            while (clock.Today <= clock.EndDate)
            {
                clock.Step();
                Console.WriteLine(clock.Today.Date.ToShortDateString() + ", " +
                    sim.Progress + "," + wt.Value + "," + leaf.LAI);
            }
            clock.Done();
            sim.Cleanup();
            //storage.Close();
            Console.WriteLine("Done");
        }

    }
}