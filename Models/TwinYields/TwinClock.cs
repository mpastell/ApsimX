using Models.Core;
using Models.Core.Run;
using Models.Interfaces;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Data;
using APSIM.Shared.Utilities;
using Docker.DotNet.Models;
using System.Threading;

namespace Models
{

    /// <summary>
    /// The clock model is resonsible for controlling the daily timestep in APSIM. It
    /// keeps track of the simulation date and loops from the start date to the end
    /// date, publishing events that other models can subscribe to.
    /// </summary>
    [Serializable]
    [PresenterName("UserInterface.Presenters.PropertyPresenter")]
    [ViewName("UserInterface.Views.PropertyView")]
    [ValidParent(ParentType = typeof(Simulation))]
    public class TwinClock : Model, IClock
    {
        /// <summary>The arguments</summary>
        private EventArgs args = new EventArgs();

        /// <summary>The summary</summary>
        [Link]
        private ISummary Summary = null;

        /// <summary>The start date of the simulation.</summary>
        [Summary]
        [Description("The start date of the simulation")]
        public DateTime? Start { get; set; }

        /// <summary>The end date of the simulation.</summary>
        [Summary]
        [Description("The end date of the simulation")]
        public DateTime? End { get; set; }

        /// <summary>
        /// Gets the start date for the simulation.
        /// </summary>
        /// <remarks>
        /// If the user did not
        /// not provide a start date, attempt to locate a weather file
        /// and use its start date. If no weather file can be found,
        /// throw an exception.
        /// </remarks>
        [JsonIgnore]
        public DateTime StartDate
        {
            get
            {
                if (Start != null)
                    return (DateTime)Start;

                // If no start date provided, try and find a weather component and use its start date.
                IWeather weather = this.FindInScope<IWeather>();
                if (weather != null)
                    return weather.StartDate;

                throw new Exception($"No start date provided in clock {this.FullPath} and no weather file could be found.");
            }
            set
            {
                Start = value;
            }
        }

        /// <summary>
        /// Gets or sets the end date for the simulation.
        /// </summary>
        /// <remarks>
        /// If the user did not
        /// not provide a end date, attempt to locate a weather file
        /// and use its end date. If no weather file can be found,
        /// throw an exception.
        /// </remarks>
        [JsonIgnore]
        public DateTime EndDate
        {
            get
            {
                if (End != null)
                    return (DateTime)End;

                // If no start date provided, try and find a weather component and use its start date.
                IWeather weather = this.FindInScope<IWeather>();
                if (weather != null)
                    return weather.EndDate;

                throw new Exception($"No end date provided in {this.FullPath}: and no weather file could be found.");
            }
            set
            {
                End = value;
            }
        }

        // Public events that we're going to publish.
        /// <summary>Occurs when [start of simulation].</summary>
        public event EventHandler StartOfSimulation;
        /// <summary>Occurs when [start of day].</summary>
        public event EventHandler StartOfDay;
        /// <summary>Occurs when [start of month].</summary>
        public event EventHandler StartOfMonth;
        /// <summary>Occurs when [start of year].</summary>
        public event EventHandler StartOfYear;
        /// <summary>Occurs when [start of week].</summary>
        public event EventHandler StartOfWeek;
        /// <summary>Occurs when [end of day].</summary>
        public event EventHandler EndOfDay;
        /// <summary>Occurs when [end of month].</summary>
        /// <summary>Occurs when [end of simulation].</summary>
        public event EventHandler EndOfSimulation;
        /// <summary>Last initialisation event.</summary>
        public event EventHandler FinalInitialise;

        /// <summary>Occurs when [do weather].</summary>
        public event EventHandler DoWeather;
        /// <summary>Occurs when [do daily initialisation].</summary>
        public event EventHandler DoDailyInitialisation;
        // /// <summary>Occurs when [do initial summary].</summary>
        //public event EventHandler DoInitialSummary;
        /// <summary>Occurs when [do management].</summary>
        public event EventHandler DoManagement;
        /// <summary>Occurs when [do PestDisease damage]</summary>
        public event EventHandler DoPestDiseaseDamage;
        /// <summary>Occurs when [do energy arbitration].</summary>
        public event EventHandler DoEnergyArbitration;                                //MicroClimate
        /// <summary>Occurs when [do soil water movement].</summary>
        public event EventHandler DoSoilWaterMovement;                                //Soil module
        /// <summary>Invoked to tell soil erosion to perform its calculations.</summary>
        public event EventHandler DoSoilErosion;
        /// <summary>Occurs when [do soil temperature].</summary>
        public event EventHandler DoSoilTemperature;
        //DoSoilNutrientDynamics will be here
        /// <summary>Occurs when [do soil organic matter].</summary>
        public event EventHandler DoSoilOrganicMatter;                                 //SurfaceOM
        /// <summary>Occurs when [do surface organic matter decomposition].</summary>
        public event EventHandler DoSurfaceOrganicMatterDecomposition;                 //SurfaceOM
        /// <summary>Occurs when [do update transpiration].</summary>
        public event EventHandler DoUpdateWaterDemand;
        /// <summary>Occurs when [do water arbitration].</summary>
        public event EventHandler DoWaterArbitration;                                  //Arbitrator
        /// <summary>Occurs between DoWaterArbitration and DoPhenology. Performs sorghum final leaf no calcs.</summary>
        public event EventHandler PrePhenology;
        /// <summary>Occurs when [do phenology].</summary>
        public event EventHandler DoPhenology;                                         // Plant
        /// <summary>Occurs when [do potential plant growth].</summary>
        public event EventHandler DoPotentialPlantGrowth;                              //Refactor to DoWaterLimitedGrowth  Plant
        /// <summary>Occurs when [do potential plant partioning].</summary>
        public event EventHandler DoPotentialPlantPartioning;                          // PMF OrganArbitrator.
        /// <summary>Occurs when [do nutrient arbitration].</summary>
        public event EventHandler DoNutrientArbitration;                               //Arbitrator
        /// <summary>Occurs when [do potential plant partioning].</summary>
        public event EventHandler DoActualPlantPartioning;                             // PMF OrganArbitrator.
        /// <summary>Occurs when [do actual plant growth].</summary>
        public event EventHandler DoActualPlantGrowth;                                 //Refactor to DoNutirentLimitedGrowth Plant
        /// <summary>Occurs when [start of simulation].</summary>
        public event EventHandler PartitioningComplete;
        /// <summary>Occurs when [do update].</summary>
        public event EventHandler DoUpdate;
        /// <summary> Process stock methods in GrazPlan Stock </summary>
        public event EventHandler DoStock;
        /// <summary> Process a Pest and Disease lifecycle object </summary>
        public event EventHandler DoLifecycle;
        /// <summary>Occurs when [do management calculations].</summary>
        public event EventHandler DoManagementCalculations;
        // /// <summary>Occurs when [do report calculations].</summary>
        // public event EventHandler DoReportCalculations;
        // /// <summary>Occurs when [do report].</summary>
        // public event EventHandler DoReport;

        /// <summary>
        /// Occurs when dcaps performs its calculations. This needs to happen
        /// between DoPotentialPlantGrowth and DoPotentialPlantPartitioning.
        /// </summary>
        public event EventHandler DoDCAPST;


        //Attempt to move events from simulation model
        /// <summary>Invoked when simulation is about to commence.</summary>
        public event EventHandler Commencing;

        /// <summary>Invoked to signal start of simulation.</summary>
        public event EventHandler<CommenceArgs> DoCommence;

        /// <summary>Start the simulation</summary>
        public void Commence(CancellationTokenSource cancelToken)
        {
            Commencing?.Invoke(this, args);
            // Begin running the simulation.
            //var cancelToken = new CancellationTokenSource();
            DoCommence?.Invoke(this, new CommenceArgs() { CancelToken = cancelToken });
        }

        // Public properties available to other models.
        /// <summary>Gets the today.</summary>
        /// <value>The today.</value>
        [JsonIgnore]
        public DateTime Today { get; private set; }

        /// <summary>
        /// Returns the current fraction of the overall simulation which has been completed
        /// </summary>
        [JsonIgnore]
        public double FractionComplete
        {
            get
            {
                if (Today == DateTime.MinValue)
                    return 0;

                TimeSpan fullSim = EndDate - StartDate;
                if (fullSim.Equals(TimeSpan.Zero))
                    return 1.0;
                else
                {
                    TimeSpan completedSpan = Today - StartDate;
                    return completedSpan.TotalDays / fullSim.TotalDays;
                }
            }
        }

        /// <summary>Is the current simulation date at end of month?</summary>
        public bool IsStartMonth => Today.Day == 1;

        /// <summary>Is the current simulation date at end of month?</summary>
        public bool IsStartYear => Today.DayOfYear == 1;

        /// <summary>Is the current simulation date at end of month?</summary>
        public bool IsEndMonth => Today.AddDays(1).Day == 1;

        /// <summary>Is the current simulation date at end of month?</summary>
        public bool IsEndYear => Today.AddDays(1).DayOfYear == 1;

        /// <summary>An event handler to allow us to initialise ourselves.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        [EventSubscribe("Commencing")]
        private void OnSimulationCommencing(object sender, EventArgs e)
        {
            Today = StartDate;
        }

        /// <summary>An event handler to signal start of a simulation.</summary>
        /// <param name="_">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        [EventSubscribe("DoCommence")]
        private void OnDoCommence(object _, CommenceArgs e)
        {
            Today = StartDate;

            //if (DoInitialSummary != null)
            //    DoInitialSummary.Invoke(this, args);

            if (StartOfSimulation != null)
                StartOfSimulation.Invoke(this, args);

            if (FinalInitialise != null)
                FinalInitialise.Invoke(this, args);
        }

        /// <summary>Progress simulation to next date</summary>
        public void Step()
        {
            //while (Today <= EndDate && (e.CancelToken == null || !e.CancelToken.IsCancellationRequested))
            //{
            if (DoWeather != null)
                DoWeather.Invoke(this, args);

            if (DoDailyInitialisation != null)
                DoDailyInitialisation.Invoke(this, args);

            if (StartOfDay != null)
                StartOfDay.Invoke(this, args);

            if (Today.Day == 1 && StartOfMonth != null)
                StartOfMonth.Invoke(this, args);

            if (Today.DayOfYear == 1 && StartOfYear != null)
                StartOfYear.Invoke(this, args);

            if (Today.DayOfWeek == DayOfWeek.Sunday && StartOfWeek != null)
                StartOfWeek.Invoke(this, args);

            if (DoManagement != null)
                DoManagement.Invoke(this, args);

            if (DoPestDiseaseDamage != null)
                DoPestDiseaseDamage.Invoke(this, args);

            if (DoEnergyArbitration != null)
                DoEnergyArbitration.Invoke(this, args);

            DoSoilErosion?.Invoke(this, args);

            if (DoSoilWaterMovement != null)
                DoSoilWaterMovement.Invoke(this, args);

            if (DoSoilTemperature != null)
                DoSoilTemperature.Invoke(this, args);

            if (DoSoilOrganicMatter != null)
                DoSoilOrganicMatter.Invoke(this, args);

            if (DoSurfaceOrganicMatterDecomposition != null)
                DoSurfaceOrganicMatterDecomposition.Invoke(this, args);

            if (DoUpdateWaterDemand != null)
                DoUpdateWaterDemand.Invoke(this, args);

            if (DoWaterArbitration != null)
                DoWaterArbitration.Invoke(this, args);

            if (PrePhenology != null)
                PrePhenology.Invoke(this, args);

            if (DoPhenology != null)
                DoPhenology.Invoke(this, args);

            if (DoPotentialPlantGrowth != null)
                DoPotentialPlantGrowth.Invoke(this, args);

            DoDCAPST?.Invoke(this, args);

            if (DoPotentialPlantPartioning != null)
                DoPotentialPlantPartioning.Invoke(this, args);

            if (DoNutrientArbitration != null)
                DoNutrientArbitration.Invoke(this, args);

            if (DoActualPlantPartioning != null)
                DoActualPlantPartioning.Invoke(this, args);

            if (DoActualPlantGrowth != null)
                DoActualPlantGrowth.Invoke(this, args);

            if (PartitioningComplete != null)
                PartitioningComplete.Invoke(this, args);

            if (DoStock != null)
                DoStock.Invoke(this, args);

            if (DoLifecycle != null)
                DoLifecycle.Invoke(this, args);

            if (DoUpdate != null)
                DoUpdate.Invoke(this, args);

            if (DoManagementCalculations != null)
                DoManagementCalculations.Invoke(this, args);

            //if (DoReportCalculations != null)
            //    DoReportCalculations.Invoke(this, args);


            if (EndOfDay != null)
                EndOfDay.Invoke(this, args);

            Today = Today.AddDays(1);
            //}
        }

        /// <summary>Finish simulation</summary>
        public void Done()
        {
            Today = EndDate;

            if (EndOfSimulation != null)
                EndOfSimulation.Invoke(this, args);
            Summary?.WriteMessage(this, "Simulation terminated normally", MessageType.Information);

        }
    }
}