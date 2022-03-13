using APSIM.Shared.Utilities;
using Models.Core;
using Models.CLEM.Resources;
using Models.CLEM.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using Models.Core.Attributes;
using System.Globalization;
using Models.CLEM.Groupings;
using Newtonsoft.Json;

namespace Models.CLEM.Activities
{
    /// <summary>Ruminant predictive stocking activity using ENSO predictions</summary>
    /// <summary>This activity will undertake stocking and destocking based on future season predictions (La Nini or El Nino)</summary>
    /// <summary>It is designed to consider individuals already marked for sale and add additional individuals before transport and sale.</summary>
    /// <summary>It will check all paddocks that the specified herd are grazing</summary>
    [Serializable]
    [ViewName("UserInterface.Views.PropertyView")]
    [PresenterName("UserInterface.Presenters.PropertyPresenter")]
    [ValidParent(ParentType = typeof(CLEMActivityBase))]
    [ValidParent(ParentType = typeof(ActivitiesHolder))]
    [ValidParent(ParentType = typeof(ActivityFolder))]
    [Description("Manage ruminant stocking based on predicted seasonal outlooks")]
    [Version(1, 0, 1, "")]
    [HelpUri(@"Content/Features/Activities/Ruminant/RuminantPredictiveStockingENSO.htm")]
    public class RuminantActivityPredictiveStockingENSO: CLEMRuminantActivityBase, ICanHandleIdentifiableChildModels
    {
        [Link]
        private Clock clock = null;
        private Relationship pastureToStockingChangeElNino { get; set; }
        private Relationship pastureToStockingChangeLaNina { get; set; }

        private double restockToSkip = 0;
        private double restockToDo = 0;
        private double destockToSkip = 0;
        private double destockToDo = 0;
        private IEnumerable<Ruminant> uniqueIndividuals;
        private IEnumerable<RuminantGroup> filterGroups = new List<RuminantGroup>();
        private List<(string paddockName, double AE, double AeChange)> paddockChanges;
        private IEnumerable<GrazeFoodStoreType> paddocks;

        /// <summary>
        /// File containing SOI measure from BOM http://www.bom.gov.au/climate/influences/timeline/
        /// Year Jan Feb Mar.....
        /// 1876 11  0.2 -3  +ve LaNina -ve El Nino
        /// </summary>
        [Description("SOI monthly data file")]
        [Required(AllowEmptyStrings = false, ErrorMessage = "SOI monthly data filename required")]
        [Models.Core.Display(Type = DisplayType.FileName)]
        public string MonthlySOIFile { get; set; }

        /// <summary>
        /// Number of previous months to consider
        /// </summary>
        [Description("Number of months to assess")]
        [Required, GreaterThanValue(0)]
        public int AssessMonths { get; set; }

        /// <summary>
        /// Mean SOI value before considered La Niña
        /// </summary>
        [Description("SOI cutoff before considered La Niña")]
        [Required, GreaterThanEqualValue(0)]
        public double SOIForLaNina { get; set; }

        /// <summary>
        /// Mean  SOI value before considered El Niño
        /// </summary>
        [Description("SOI cutoff (-ve) before considered El Niño")]
        [Required, GreaterThanEqualValue(0)]
        public double SOIForElNino { get; set; }

        /// <summary>
        /// Minimum estimated feed (kg/ha) before restocking
        /// </summary>
        [Description("Minimum estimated feed (kg/ha) before restocking")]
        [Required, GreaterThanEqualValue(0)]
        public double MinimumFeedBeforeRestock { get; set; }

        /// <summary>
        /// AE to destock
        /// </summary>
        [JsonIgnore]
        public double AeToDestock { get; private set; }

        /// <summary>
        /// AE destocked
        /// </summary>
        [JsonIgnore]
        public double AeDestocked { get; private set; }

        /// <summary>
        /// AE destock shortfall
        /// </summary>
        public double AeShortfall { get { return AeToDestock - AeDestocked; } }

        /// <summary>
        /// AE to restock
        /// </summary>
        [JsonIgnore]
        public double AeToRestock { get; private set; }

        /// <summary>
        /// AE restocked
        /// </summary>
        [JsonIgnore]
        public double AeRestocked { get; private set; }

        private string fullFilename;
        private Dictionary<DateTime, double> ForecastSequence;

        /// <summary>
        /// Constructor
        /// </summary>
        public RuminantActivityPredictiveStockingENSO()
        {
            this.SetDefaults();
            TransactionCategory = "Livestock.Manage.[Destock]";
        }

        private ENSOState GetENSOMeasure()
        {
            // gets the average monthly Southern Oscillation Index from last six months
            // won't work for start of record (1876)
            // if average >= 7 assumed La Nina. If <= 7 El Nino, else Neutral
            // http://www.bom.gov.au/climate/influences/timeline/

            DateTime date = new DateTime(clock.Today.Year, clock.Today.Month, 1);
            int monthsAvailable = ForecastSequence.Where(a => a.Key >= date && a.Key <= date.AddMonths(-6)).Count();
            // get sum of previous 6 months
            double ensoValue = ForecastSequence.Where(a => a.Key >= date && a.Key <= date.AddMonths(-6)).Sum(a => a.Value);
            // get average SIOIndex
            ensoValue /= monthsAvailable;
            if (ensoValue <= SOIForElNino)
                return ENSOState.ElNino;
            else if (ensoValue >= SOIForLaNina)
                return ENSOState.LaNina;
            else
                return ENSOState.Neutral;
        }

        /// <inheritdoc/>
        public override LabelsForIdentifiableChildren DefineIdentifiableChildModelLabels(string type)
        {
            switch (type)
            {
                case "RuminantGroup":
                    return new LabelsForIdentifiableChildren(
                        identifiers: new List<string>(),
                        units: new List<string>()
                        );
                case "RuminantActivityFee":
                case "LabourRequirement":
                    return new LabelsForIdentifiableChildren(
                        identifiers: new List<string>() {
                            "Destock required",
                            "Restock required"
                        },
                        units: new List<string>() {
                            "fixed",
                            "per AE"
                        }
                        );
                default:
                    return new LabelsForIdentifiableChildren();
            }
        }

        /// <summary>An event handler to allow us to initialise ourselves.</summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        [EventSubscribe("CLEMInitialiseActivity")]
        private void OnCLEMInitialiseActivity(object sender, EventArgs e)
        {
            ForecastSequence = new Dictionary<DateTime, double>();

            Simulation simulation = FindAncestor<Simulation>();
            if (simulation != null)
                fullFilename = PathUtilities.GetAbsolutePath(this.MonthlySOIFile, simulation.FileName);
            else
                fullFilename = this.MonthlySOIFile;

            //check file exists
            if (File.Exists(fullFilename))
            {
                // load ENSO file into memory
                using (StreamReader ensoStream = new StreamReader(MonthlySOIFile))
                {
                    string line = "";
                    while ((line = ensoStream.ReadLine()) != null)
                    {
                        if (!line.StartsWith("Year"))
                        {
                            string[] items = line.Split(' ');
                            for (int i = 1; i < items.Count(); i++)
                            {
                                ForecastSequence.Add(
                                    new DateTime(Convert.ToInt16(items[0]), Convert.ToInt16(i, CultureInfo.InvariantCulture), 1),
                                    Convert.ToDouble(items[i], CultureInfo.InvariantCulture)
                                    );
                            }
                        }
                    }
                }
            }
            else
                Summary.WriteMessage(this, String.Format("Could not find ENSO-SOI datafile [x={0}] for [a={1}]", MonthlySOIFile, this.Name), MessageType.Error);

            this.InitialiseHerd(false, true);

            // try attach relationships
            pastureToStockingChangeElNino = this.FindAllChildren<Relationship>().Where(a => a.Name.ToLower().Contains("nino")).FirstOrDefault();
            pastureToStockingChangeLaNina = this.FindAllChildren<Relationship>().Where(a => a.Name.ToLower().Contains("nina")).FirstOrDefault();

            filterGroups = GetIdentifiableChildrenByIdentifier<RuminantGroup>(true, false);
            paddocks = Resources.FindResourceGroup<GrazeFoodStore>()?.FindAllChildren<GrazeFoodStoreType>();
            paddockChanges = new List<(string paddockName, double AE, double AeShortfall)>();
        }

        /// <inheritdoc/>
        [EventSubscribe("CLEMAnimalStock")]
        protected override void OnGetResourcesPerformActivity(object sender, EventArgs e)
        {
            ManageActivityResourcesAndTasks();
        }

        /// <inheritdoc/>
        public override List<ResourceRequest> RequestResourcesForTimestep(double argument = 0)
        {
            paddockChanges.Clear();
            AeToDestock = 0;
            AeDestocked = 0;
            AeToRestock = 0;
            AeRestocked = 0;
            restockToSkip = 0;
            restockToDo = 0;
            destockToSkip = 0;
            destockToDo = 0;
            IEnumerable<Ruminant> herd = GetIndividuals<Ruminant>(GetRuminantHerdSelectionStyle.AllOnFarm).Where(a => (a.Location ?? "") != "");
            uniqueIndividuals = GetUniqueIndividuals<Ruminant>(filterGroups, herd);

            // Get ENSO forcase for current time
            ENSOState forecastEnsoState = GetENSOMeasure();

            foreach (GrazeFoodStoreType pasture in paddocks)
            {
                IEnumerable<Ruminant> paddockIndividuals = uniqueIndividuals.Where(a => a.Location == pasture.Name);

                double aELocationNeeded = 0;

                // total adult equivalents of all breeds on pasture for utilisation
                double totalAE = paddockIndividuals.Sum(a => a.AdultEquivalent);
                // determine AE marked for sale and purchase of managed herd
                double markedForSaleAE = paddockIndividuals.Where(a => a.ReadyForSale).Sum(a => a.AdultEquivalent);
                double purchaseAE = HerdResource.PurchaseIndividuals.Where(a => a.Location == pasture.Name).Sum(a => a.AdultEquivalent);

                double herdChange = 1.0;
                bool relationshipFound = false;
                switch (forecastEnsoState)
                {
                    case ENSOState.Neutral:
                        break;
                    case ENSOState.ElNino:
                        if (!(pastureToStockingChangeElNino is null))
                        {
                            double kgha = pasture.TonnesPerHectare * 1000;
                            herdChange = pastureToStockingChangeElNino.SolveY(kgha);
                            relationshipFound = true;
                        }
                        break;
                    case ENSOState.LaNina:
                        if (!(pastureToStockingChangeLaNina is null))
                        {
                            double kgha = pasture.TonnesPerHectare * 1000;
                            herdChange = pastureToStockingChangeLaNina.SolveY(kgha);
                            relationshipFound = true;
                        }
                        break;
                    default:
                        break;
                }
                if (!relationshipFound)
                {
                    string warn = $"No pasture biomass to herd change proportion [Relationship] provided for {((forecastEnsoState == ENSOState.ElNino) ? "El Niño" : "La Niña")} phase in [a={this.Name}]\r\nNo stock management will be performed in this phase.";
                    this.Status = ActivityStatus.Warning;
                    Warnings.CheckAndWrite(warn, Summary, this, MessageType.Warning);
                }

                if (MathUtilities.IsGreaterThan(herdChange, 1.0))
                {
                    aELocationNeeded = Math.Max(0, (totalAE * herdChange) - purchaseAE);
                    AeToRestock += aELocationNeeded;
                    paddockChanges.Add((pasture.Name, totalAE, aELocationNeeded));
                }
                else if (MathUtilities.IsLessThan(herdChange, 1.0))
                {
                    aELocationNeeded = Math.Max(0, (totalAE * (1 - herdChange)) - markedForSaleAE);
                    AeToDestock += aELocationNeeded;
                    paddockChanges.Add((pasture.Name, totalAE, -1*aELocationNeeded));
                }
                else
                {
                    paddockChanges.Add((pasture.Name, 0, 0));
                }
            }

            // provide updated units of measure for identifiable children
            foreach (var valueToSupply in valuesForIdentifiableModels.ToList())
            {
                switch (valueToSupply.Key.identifier)
                {
                    case "Destock required":
                        switch (valueToSupply.Key.unit)
                        {
                            case "fixed":
                                valuesForIdentifiableModels[valueToSupply.Key] = 1;
                                break;
                            case "per AE":
                                valuesForIdentifiableModels[valueToSupply.Key] = AeToDestock;
                                break;
                            default:
                                throw new NotImplementedException(UnknownUnitsErrorText(this, valueToSupply.Key));
                        }
                        break;
                    case "Restock required":
                        switch (valueToSupply.Key.unit)
                        {
                            case "fixed":
                                valuesForIdentifiableModels[valueToSupply.Key] = 1;
                                break;
                            case "per AE":
                                valuesForIdentifiableModels[valueToSupply.Key] = AeToRestock;
                                break;
                            default:
                                throw new NotImplementedException(UnknownUnitsErrorText(this, valueToSupply.Key));
                        }
                        break;
                    default:
                        throw new NotImplementedException(UnknownIdentifierErrorText(this, valueToSupply.Key));
                }
            }
            return null;
        }

        /// <inheritdoc/>
        protected override void AdjustResourcesForTimestep()
        {
            IEnumerable<ResourceRequest> shortfalls = MinimumShortfallProportion();
            if (shortfalls.Any())
            {
                // find shortfall by identifiers as these may have different influence on outcome
                var destockShort = shortfalls.Where(a => a.IdentifiableChildDetails.identifier == "Destock required").FirstOrDefault();
                if (destockShort != null)
                    destockToSkip = Convert.ToInt32(destockToDo * destockShort.Required / destockShort.Provided);

                var restockShort = shortfalls.Where(a => a.IdentifiableChildDetails.identifier == "Restock required").FirstOrDefault();
                if (restockShort != null)
                    restockToSkip = Convert.ToInt32(restockToDo * restockShort.Required / restockShort.Provided);

                this.Status = ActivityStatus.Partial;
            }
        }

        /// <inheritdoc/>
        public override void PerformTasksForTimestep(double argument = 0)
        {
            // TODO: allow movement of animals between paddocks if will solve the problem

            // destocking needed
            double destockDone = 0;
            if (MathUtilities.IsPositive(destockDone))
            {
                foreach (var paddock in paddockChanges.Where(a => MathUtilities.IsNegative(a.AeChange)))
                {
                    HerdResource.PurchaseIndividuals.RemoveAll(a => a.Location == paddock.paddockName);

                    foreach (Ruminant ruminant in uniqueIndividuals.Where(a => a.Location == paddock.paddockName).ToList())
                    {
                        if (MathUtilities.IsLessThanOrEqual(destockDone, destockToDo - destockToSkip))
                        {
                            destockDone = 0;
                            break;
                        }

                        if (ruminant.SaleFlag != HerdChangeReason.DestockSale)
                        {
                            destockDone += ruminant.AdultEquivalent;
                            ruminant.SaleFlag = HerdChangeReason.DestockSale;
                        }
                    }
                }
                AeDestocked = destockDone;
            }

            // restocking
            double restockDone = 0;
            if (MathUtilities.IsPositive(restockDone))
            {
                foreach (var paddock in paddockChanges.Where(a => MathUtilities.IsPositive(a.AeChange)))
                {
                    // ensure min pasture for restocking
                    GrazeFoodStoreType pasture = paddocks.Where(a => a.Name == paddock.paddockName).FirstOrDefault();
                    if (pasture != null && MathUtilities.IsGreaterThanOrEqual(pasture.TonnesPerHectare * 1000, MinimumFeedBeforeRestock))
                    {
                        var specifyComponents = FindAllChildren<SpecifyRuminant>();
                        if (specifyComponents.Count() == 0)
                        {
                            string warn = $"No [f=SpecifyRuminant]s were provided in [a={this.Name}]\r\nNo restocking will be performed.";
                            this.Status = ActivityStatus.Warning;
                            Warnings.CheckAndWrite(warn, Summary, this, MessageType.Warning);
                        }

                        // buy animals specified in restock ruminant groups
                        foreach (SpecifyRuminant item in specifyComponents)
                        {
                            double sumAE = 0;
                            double aEToBuy = paddock.AeChange;
                            double limitAE = aEToBuy * item.Proportion;

                            while (MathUtilities.IsLessThanOrEqual(restockDone, restockToDo - restockToSkip) && MathUtilities.IsLessThan(sumAE, limitAE) && MathUtilities.IsPositive(aEToBuy))
                            {
                                Ruminant newIndividual = item.Details.CreateIndividuals(1, null).FirstOrDefault();
                                newIndividual.Location = pasture.Name;
                                newIndividual.BreedParams = item.BreedParams;
                                newIndividual.HerdName = item.BreedParams.Name;
                                newIndividual.PurchaseAge = newIndividual.Age;
                                newIndividual.SaleFlag = HerdChangeReason.RestockPurchase;

                                if (MathUtilities.FloatsAreEqual(newIndividual.Weight, 0))
                                {
                                    throw new ApsimXException(this, $"Specified individual added during restock cannot have no weight in [{this.Name}]");
                                }

                                HerdResource.PurchaseIndividuals.Add(newIndividual);
                                double indAE = newIndividual.AdultEquivalent;
                                aEToBuy -= indAE;
                                sumAE += indAE;
                                restockDone += indAE;
                            }
                            if (MathUtilities.IsNegative(restockDone))
                                restockDone = 0;
                        }
                    }
                }
                AeRestocked = restockDone;
            }

            if (MathUtilities.IsPositive(destockDone + restockDone))
            { 
                if (MathUtilities.FloatsAreEqual(destockToDo + restockToDo, destockDone + restockDone))
                    SetStatusSuccessOrPartial();
                else
                    this.Status = ActivityStatus.Partial;

                // TODO add resport status
                // fire event to allow reporting of findings
                //OnReportStatus(new EventArgs());
            }

        }

        ///// <summary>An event handler to call for all resources other than food for feeding activity</summary>
        ///// <param name="sender">The sender.</param>
        ///// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        //[EventSubscribe("CLEMAnimalStock")]
        //private void OnCLEMAnimalStock(object sender, EventArgs e)
        //{
        //    AeToDestock = 0;
        //    AeDestocked = 0;
        //    AeToRestock = 0;
        //    AeRestocked = 0;

        //    // this event happens after management has marked individuals for purchase or sale.
        //    if (this.TimingOK)
        //    {
        //        // Get ENSO forcase for current time
        //        ENSOState forecastEnsoState = GetENSOMeasure();

        //        this.Status = ActivityStatus.NotNeeded;

        //        // calculate dry season pasture available for each managed paddock holding stock
        //        foreach (var newgroup in HerdResource.Herd.Where(a => a.Location != "").GroupBy(a => a.Location))
        //        {
        //            double aELocationNeeded = 0;

        //            // total adult equivalents of all breeds on pasture for utilisation
        //            double totalAE = newgroup.Sum(a => a.AdultEquivalent);
        //            // determine AE marked for sale and purchase of managed herd
        //            double markedForSaleAE = newgroup.Where(a => a.ReadyForSale).Sum(a => a.AdultEquivalent);
        //            double purchaseAE = HerdResource.PurchaseIndividuals.Where(a => a.Location == newgroup.Key).Sum(a => a.AdultEquivalent);

        //            double herdChange = 1.0;
        //            bool relationshipFound = false;
        //            switch (forecastEnsoState)
        //            {
        //                case ENSOState.Neutral:
        //                    break;
        //                case ENSOState.ElNino:
        //                    if (!(pastureToStockingChangeElNino is null))
        //                    {
        //                        GrazeFoodStoreType pasture = Resources.FindResourceType<GrazeFoodStore, GrazeFoodStoreType>(this, newgroup.Key, OnMissingResourceActionTypes.Ignore, OnMissingResourceActionTypes.Ignore);
        //                        double kgha = pasture.TonnesPerHectare * 1000;
        //                        herdChange = pastureToStockingChangeElNino.SolveY(kgha);
        //                        relationshipFound = true;
        //                    }
        //                    break;
        //                case ENSOState.LaNina:
        //                    if (!(pastureToStockingChangeLaNina is null))
        //                    {
        //                        GrazeFoodStoreType pasture = Resources.FindResourceType<GrazeFoodStore, GrazeFoodStoreType>(this, newgroup.Key, OnMissingResourceActionTypes.Ignore, OnMissingResourceActionTypes.Ignore);
        //                        double kgha = pasture.TonnesPerHectare * 1000;
        //                        herdChange = pastureToStockingChangeLaNina.SolveY(kgha);
        //                        relationshipFound = true;
        //                    }
        //                    break;
        //                default:
        //                    break;
        //            }
        //            if (!relationshipFound)
        //            {
        //                string warn = $"No pasture biomass to herd change proportion [Relationship] provided for {((forecastEnsoState== ENSOState.ElNino)? "El Niño":"La Niña")} phase in [a={this.Name}]\r\nNo stock management will be performed in this phase.";
        //                this.Status = ActivityStatus.Warning;
        //                Warnings.CheckAndWrite(warn, Summary, this, MessageType.Warning);
        //            }

        //            if (MathUtilities.IsGreaterThan(herdChange, 1.0))
        //            {
        //                aELocationNeeded = Math.Max(0, (totalAE * herdChange) - purchaseAE);
        //                AeToRestock += aELocationNeeded;
        //                double notHandled = HandleRestocking(aELocationNeeded, newgroup.Key, newgroup.FirstOrDefault());
        //                AeRestocked += (aELocationNeeded - notHandled);
        //            }
        //            else if(MathUtilities.IsLessThan(herdChange,1.0))
        //            {
        //                aELocationNeeded = Math.Max(0, (totalAE * (1 - herdChange)) - markedForSaleAE);
        //                AeToDestock += aELocationNeeded;
        //                double notHandled = HandleDestocking(AeToDestock, newgroup.Key);
        //                AeDestocked += (aELocationNeeded - notHandled);
        //            }
        //        }

        //        if(this.Status != ActivityStatus.Warning & AeToDestock + AeToRestock > 0)
        //        {
        //            if(MathUtilities.FloatsAreEqual(Math.Max(0,AeToRestock - AeRestocked) + Math.Max(0, AeToDestock - AeDestocked), 0))
        //                this.Status = ActivityStatus.Success;
        //            else
        //                this.Status = ActivityStatus.Partial;
        //        }
        //    }
        //}

        ///// <summary>
        ///// Method to perform destocking
        ///// </summary>
        ///// <param name="animalEquivalentsForSale"></param>
        ///// <param name="paddockName"></param>
        ///// <returns>The AE that were not handled</returns>
        //private double HandleDestocking(double animalEquivalentsForSale, string paddockName)
        //{
        //    if (animalEquivalentsForSale <= 0)
        //        return 0;

        //    // move to underutilised paddocks
        //    // TODO: This can be added later as an activity including spelling

        //    // remove all potential purchases from list as they can't be supported.
        //    // This does not change the shortfall AE as they were not counted in TotalAE pressure.
        //    HerdResource.PurchaseIndividuals.RemoveAll(a => a.Location == paddockName);

        //    var destockGroups = FindAllChildren<RuminantGroup>();
        //    if (!destockGroups.Any())
        //    {
        //        string warn = $"No [f=FilterGroup]s with were provided in [a={this.Name}]\r\nNo destocking will be performed.";
        //        this.Status = ActivityStatus.Warning;
        //        Warnings.CheckAndWrite(warn, Summary, this, MessageType.Warning);
        //    }

        //    foreach (var item in destockGroups)
        //    {
        //        // works with current filtered herd to obey filtering.
        //        var herd = item.Filter(CurrentHerd(false))
        //            .Where(a => a.Location == paddockName && !a.ReadyForSale);

        //        foreach (Ruminant ruminant in herd)
        //        {
        //            if (ruminant.SaleFlag != HerdChangeReason.DestockSale)
        //            {
        //                animalEquivalentsForSale -= ruminant.AdultEquivalent;
        //                ruminant.SaleFlag = HerdChangeReason.DestockSale;
        //            }

        //            if (MathUtilities.IsLessThanOrEqual(animalEquivalentsForSale, 0))
        //            {
        //                this.Status = ActivityStatus.Success;
        //                return 0;
        //            }
        //        }
        //    }

        //    return animalEquivalentsForSale;

        //    // handling of sucklings with sold female is in RuminantActivityBuySell
        //    // buy or sell is handled by the buy sell activity
        //}

        //private double HandleRestocking(double animalEquivalentsToBuy, string paddockName, Ruminant exampleRuminant)
        //{
        //    if (animalEquivalentsToBuy <= 0) 
        //        return 0;

        //    GrazeFoodStoreType foodStore = Resources.FindResourceType<GrazeFoodStore, GrazeFoodStoreType>(this, paddockName, OnMissingResourceActionTypes.Ignore, OnMissingResourceActionTypes.ReportErrorAndStop);

        //    // ensure min pasture for restocking
        //    if ((foodStore == null) || MathUtilities.IsGreaterThan(foodStore.TonnesPerHectare * 1000, MinimumFeedBeforeRestock))
        //    {
        //        var specifyComponents = FindAllChildren<SpecifyRuminant>();
        //        if (specifyComponents.Count() == 0)
        //        {
        //            string warn = $"No [f=SpecifyRuminant]s were provided in [a={this.Name}]\r\nNo restocking will be performed.";
        //            this.Status = ActivityStatus.Warning;
        //            Warnings.CheckAndWrite(warn, Summary, this, MessageType.Warning);
        //        }

        //        // buy animals specified in restock ruminant groups
        //        foreach (SpecifyRuminant item in specifyComponents)
        //        {
        //            double sumAE = 0;
        //            double limitAE = animalEquivalentsToBuy * item.Proportion;

        //            while (MathUtilities.IsLessThan(sumAE, limitAE) && MathUtilities.IsPositive(animalEquivalentsToBuy))
        //            {
        //                Ruminant newIndividual = item.Details.CreateIndividuals(1, null).FirstOrDefault();
        //                newIndividual.Location = paddockName;
        //                newIndividual.BreedParams = item.BreedParams;
        //                newIndividual.HerdName = item.BreedParams.Name;
        //                newIndividual.PurchaseAge = newIndividual.Age;
        //                newIndividual.SaleFlag = HerdChangeReason.RestockPurchase;

        //                if(MathUtilities.FloatsAreEqual(newIndividual.Weight, 0))
        //                {
        //                    throw new ApsimXException(this, $"Specified individual added during restock cannot have no weight in [{this.Name}]");
        //                }

        //                HerdResource.PurchaseIndividuals.Add(newIndividual);
        //                double indAE = newIndividual.AdultEquivalent;
        //                animalEquivalentsToBuy -= indAE;
        //                sumAE += indAE;
        //            }
        //        }
        //        return Math.Max(0,animalEquivalentsToBuy);
        //    }
        //    return animalEquivalentsToBuy;
        //}

        #region descriptive summary

        /// <inheritdoc/>
        public override string ModelSummary()
        {
            using (StringWriter htmlWriter = new StringWriter())
            {
                bool extracomps = false;
                htmlWriter.Write("\r\n<div class=\"activityentry\">Monthly SOI data are provided by ");
                if (MonthlySOIFile == null || MonthlySOIFile == "")
                    htmlWriter.Write("<span class=\"errorlink\">File not set</span>");
                else
                    htmlWriter.Write("<span class=\"filelink\">" + MonthlySOIFile + "</span>");

                htmlWriter.Write("</div>");
                htmlWriter.Write("\r\n<div class=\"activityentry\">The mean of the previous ");
                if(AssessMonths == 0)
                    htmlWriter.Write("<span class=\"errorlink\">Not set</span>");
                else
                    htmlWriter.Write($"<span class=\"setvalue\">{AssessMonths}</span>");

                htmlWriter.Write($" months will determine the current ENSO phase where:");
                htmlWriter.Write("</div>");

                // when in El Nino
                htmlWriter.Write("\r\n<div class=\"activitybannerlight\">El Ni&ntilde;o phase</div>");
                htmlWriter.Write("\r\n<div class=\"activitycontentlight\">");
                htmlWriter.Write($"\r\n<div class=\"activityentry\">Mean SOI less than <span class=\"setvalue\">{SOIForElNino}</span></div>");

                // relationship to use
                var relationship = this.FindAllChildren<Relationship>().Where(a => a.Name.ToLower().Contains("nino")).FirstOrDefault();
                if(relationship is null)
                    htmlWriter.Write($"\r\n<div class=\"activityentry\"><span class=\"errorlink\">No <span class=\"otherlink\">Relationship</span> provided!</span> No herd change will be calculated for this phase</div>");
                else
                {
                    extracomps = true;
                    htmlWriter.Write($"\r\n<div class=\"activityentry\">Herd change will be calculated from <span class=\"otherlink\">Relationship.{relationship.Name}</span> provided below</div>");
                }

                htmlWriter.Write("</div>");


                // when in La Nina
                htmlWriter.Write("\r\n<div class=\"activitybannerlight\">La Ni&ntilde;a phase</div>");
                htmlWriter.Write("\r\n<div class=\"activitycontentlight\">");
                htmlWriter.Write($"\r\n<div class=\"activityentry\">Mean SOI greater than <span class=\"setvalue\">{SOIForLaNina}</span></div>");

                // relationship to use
                relationship = this.FindAllChildren<Relationship>().Where(a => a.Name.ToLower().Contains("nina")).FirstOrDefault();
                if (relationship is null)
                    htmlWriter.Write($"\r\n<div class=\"activityentry\"><span class=\"errorlink\">No <span class=\"otherlink\">Relationship</span> provided!</span> No herd change will be calculated for this phase</div>");
                else
                {
                    extracomps = true;
                    htmlWriter.Write($"\r\n<div class=\"activityentry\">Herd change will be calculated from <span class=\"otherlink\">Relationship.{relationship.Name}</span> provided below</div>");
                }

                htmlWriter.Write("</div>");

                htmlWriter.Write("\r\n<div class=\"activitybannerlight\">Herd change</div>");
                // Destock
                htmlWriter.Write("\r\n<div class=\"activitycontentlight\">");
                var rumGrps = FindAllChildren<RuminantGroup>();
                if (rumGrps.Count() == 0)
                    htmlWriter.Write($"\r\n<div class=\"activityentry\"><span class=\"errorlink\">No <span class=\"filterlink\">RuminantGroups</span> were provided</span>. No destocking will be performed</div>");
                else
                {
                    extracomps = true;
                    htmlWriter.Write($"\r\n<div class=\"activityentry\">Destocking will be performed in the order of <span class=\"filterlink\">RuminantGroups</span> with Reason <span class=\"setvalue\">Destock</span> provided below</div>");
                }

                // restock
                // pasture
                var specs = FindAllChildren<SpecifyRuminant>();
                if(specs.Count() == 0)
                    htmlWriter.Write($"\r\n<div class=\"activityentry\"><span class=\"errorlink\">No <span class=\"resourcelink\">SpecifyRuminant</span> were provided</span>. No restocking will be performed</div>");
                else
                {
                    extracomps = true;
                    htmlWriter.Write($"\r\n<div class=\"activityentry\">Restocking will be performed in the order of <span class=\"resourcelink\">SpecifyRuminant</span> provided below</div>");
                    htmlWriter.Write($"\r\n<div class=\"activityentry\">Restocking will be only take place when pasture biomass is above {MinimumFeedBeforeRestock} kg per hectare</div>");
                }
                htmlWriter.Write("</div>");

                htmlWriter.Write("\r\n<div style=\"margin-top:10px;\" class=\"activitygroupsborder\">");
                if (extracomps)
                    htmlWriter.Write("<div class=\"labournote\">Additional components used by this activity</div>");
                else
                    htmlWriter.Write("<div class=\"labournote\">No additional components have been supplied</div>");

                return htmlWriter.ToString();
            }
        }

        /// <inheritdoc/>
        public override string ModelSummaryInnerClosingTags()
        {
            return "</div>";
        }
        #endregion
    }

    /// <summary>
    /// ENSO state
    /// </summary>
    public enum ENSOState
    {
        /// <summary>
        /// Neutral conditions
        /// </summary>
        Neutral,
        /// <summary>
        /// El Nino conditions
        /// </summary>
        ElNino,
        /// <summary>
        /// La Nina conditions
        /// </summary>
        LaNina

    }

}
