﻿using Models.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Models;
using APSIM.Shared.Utilities;
using System.Data;
using System.IO;
using Models.CLEM.Resources;
using Models.Core.Attributes;
using Models.Core.Run;
using Models.Storage;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace Models.CLEM.Reporting
{
    /// <summary>
    /// A report class for writing resource balances to the data store.
    /// </summary>
    [Serializable]
    [ViewName("UserInterface.Views.ReportView")]
    [PresenterName("UserInterface.Presenters.CLEMReportResultsPresenter")]
    [ValidParent(ParentType = typeof(ZoneCLEM))]
    [ValidParent(ParentType = typeof(CLEMFolder))]
    [ValidParent(ParentType = typeof(Folder))]
    [Description("This report automatically generates a current balance column for each CLEM Resource Type\r\nassociated with the CLEM Resource Groups specified (name only) in the variable list.")]
    [Version(1, 0, 3, "Respects herd transaction style in reporting herd breakdown columns")]
    [Version(1, 0, 2, "Includes value as reportable columns")]
    [Version(1, 0, 1, "")]
    [HelpUri(@"Content/Features/Reporting/ResourceBalances.htm")]
    public class ReportResourceBalances: Models.Report
    {
        [Link]
        private ResourcesHolder Resources = null;
        [Link]
        private Summary Summary = null;

        /// <summary>
        /// Gets or sets report groups for outputting
        /// </summary>
        [Description("Resource groups")]
        //[Display(Type = DisplayType.MultiLineText)]
        [Category("General", "Resources")]
        [Required(AllowEmptyStrings = false, ErrorMessage = "At least one Resource group must be provided for the Balances Report")]
        public string[] ResourceGroupsToRport { get; set; }

        /// <summary>
        /// Report balances of amount
        /// </summary>
        [Category("Report", "General")]
        [Description("Report physical amount")]
        public bool ReportAmount { get; set; }

        /// <summary>
        /// Report balances of value
        /// </summary>
        [Category("Report", "Economics")]
        [Description("Report dollar value")]
        public bool ReportValue { get; set; }

        /// <summary>
        /// Report balances of animal equivalents
        /// </summary>
        [Category("Report", "Ruminants")]
        [Description("Report ruminant Adult Equivalents")]
        public bool ReportAnimalEquivalents { get; set; }

        /// <summary>
        /// Report balances of animal weight
        /// </summary>
        [Category("Report", "Ruminants")]
        [Description("Report Ruminant total weight")]
        public bool ReportAnimalWeight { get; set; }

        /// <summary>
        /// Report available land as balance
        /// </summary>
        [Category("Report", "Land")]
        [Description("Report Land as area present")]
        public bool ReportLandEntire { get; set; }

        /// <summary>
        /// Report available labour in individuals
        /// </summary>
        [Category("Report", "Land")]
        [Description("Report Labour as individuals")]
        public bool ReportLabourIndividuals { get; set; }

        private IEnumerable<IActivityTimer> timers;

        /// <summary>
        /// Constructor
        /// </summary>
        public ReportResourceBalances()
        {
            ReportAmount = true;
        }

        /// <summary>An event handler to allow us to initialize ourselves.</summary>
        /// <param name="sender">Event sender</param>
        /// <param name="e">Event arguments</param>
        [EventSubscribe("FinalInitialise")] // "Commencing"
        private void OnCommencing(object sender, EventArgs e)
        {
            if (ResourceGroupsToRport is null)
                return; 
            
            timers = FindAllChildren<IActivityTimer>();

            dataToWriteToDb = null;
            // sanitise the variable names and remove duplicates
            
            List<string> variableNames = new List<string>();
            if (ResourceGroupsToRport.Where(a => a.Contains("[Clock].Today")).Any() is false)
            {
                variableNames.Add("[Clock].Today as Date");
            }

            if (ResourceGroupsToRport != null)
            {
                for (int i = 0; i < this.ResourceGroupsToRport.Length; i++)
                {
                    // each variable name is now a ResourceGroup
                    bool isDuplicate = StringUtilities.IndexOfCaseInsensitive(variableNames, this.ResourceGroupsToRport[i].Trim()) != -1;
                    if (!isDuplicate && this.ResourceGroupsToRport[i] != string.Empty)
                    {
                        if (this.ResourceGroupsToRport[i].StartsWith("["))
                        {
                            variableNames.Add(this.ResourceGroupsToRport[i]);
                        }
                        else
                        {
                            // check it is a ResourceGroup
                            CLEMModel model = Resources.GetResourceGroupByName(this.ResourceGroupsToRport[i]) as CLEMModel;
                            if (model == null)
                            {
                                Summary.WriteWarning(this, $"Invalid resource group [r={this.ResourceGroupsToRport[i]}] in ReportResourceBalances [{this.Name}]{Environment.NewLine}Entry has been ignored");
                            }
                            else
                            {
                                if (model.GetType().Name == "Labour")
                                {
                                    string amountStr = "Amount";
                                    if (ReportLabourIndividuals)
                                    {
                                        amountStr = "Individuals";
                                    }

                                    for (int j = 0; j < (model as Labour).Items.Count; j++)
                                    {
                                        if (ReportAmount)
                                        {
                                            variableNames.Add("[Resources]." + this.ResourceGroupsToRport[i] + ".Items[" + (j + 1).ToString() + $"].{amountStr} as " + (model as Labour).Items[j].Name); 
                                        }

                                        //TODO: what economic metric is needed for labour
                                        //TODO: add ability to report labour value if required
                                    }
                                }
                                else
                                {
                                    // get all children
                                    foreach (CLEMModel item in model.Children.Where(a => a.GetType().IsSubclassOf(typeof(CLEMModel)))) // this.FindAllChildren<CLEMModel>()) //
                                    {
                                        string amountStr = "Amount";
                                        switch (item.GetType().Name)
                                        {
                                            case "FinanceType":
                                                amountStr = "Balance";
                                                break;
                                            case "LandType":
                                                if (ReportLandEntire)
                                                {
                                                    amountStr = "LandArea";
                                                }
                                                break;
                                            default:
                                                break;
                                        }
                                        if (item.GetType().Name == "RuminantType")
                                        {
                                            // add each variable needed
                                            foreach (var category in (model as RuminantHerd).GetReportingGroups(item as RuminantType))
                                            {
                                                if (ReportAmount)
                                                {
                                                    variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.GetRuminantReportGroup(\"{(item as IModel).Name}\",\"{category}\").Count as {item.Name.Replace(" ", "_")}{(((model as RuminantHerd).TransactionStyle != RuminantTransactionsGroupingStyle.Combined) ? $".{category.Replace(" ", "_")}" : "")}.Count");
                                                }
                                                if (ReportAnimalEquivalents)
                                                {
                                                    variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.GetRuminantReportGroup({(item as IModel).Name},{category}).TotalAE as {item.Name.Replace(" ", "_")}{(((model as RuminantHerd).TransactionStyle != RuminantTransactionsGroupingStyle.Combined) ? $".{category.Replace(" ", "_")}" : "")}.AE");
                                                }
                                                if (ReportAnimalWeight)
                                                {
                                                    variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.GetRuminantReportGroup({(item as IModel).Name},{category}).TotalWeight as {item.Name.Replace(" ", "_")}{(((model as RuminantHerd).TransactionStyle != RuminantTransactionsGroupingStyle.Combined) ? $".{category.Replace(" ", "_")}" : "")}.Weight");
                                                }
                                                if (ReportValue)
                                                {
                                                    variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.GetRuminantReportGroup({(item as IModel).Name},{category}).TotalValue as {item.Name.Replace(" ", "_")}{(((model as RuminantHerd).TransactionStyle != RuminantTransactionsGroupingStyle.Combined) ? $".{category.Replace(" ", "_")}" : "")}.Value");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (ReportAmount)
                                            {
                                                variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.{ item.Name}.{ amountStr } as { item.Name.Replace(" ", "_") }_Amount");
                                            }
                                            if (ReportValue & item.GetType().Name != "FinanceType")
                                            {
                                                variableNames.Add($"[Resources].{this.ResourceGroupsToRport[i]}.{ item.Name}.CalculateValue({ $"[Resources].{this.ResourceGroupsToRport[i]}.{ item.Name}.{ amountStr }" }, False) as { item.Name.Replace(" ", "_") }_Value");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            VariableNames = variableNames.ToArray();
            // Tidy up variable/event names.
            VariableNames = TidyUpVariableNames();
            EventNames = TidyUpEventNames();
            this.FindVariableMembers();

            if (EventNames.Length == 0 || EventNames[0] == "")
            {
                events.Subscribe("[Clock].CLEMEndOfTimeStep", DoOutputEvent);
            }
            else
            {
                // Subscribe to events.
                foreach (string eventName in EventNames)
                {
                    if (eventName != string.Empty)
                    {
                        events.Subscribe(eventName.Trim(), DoOutputEvent);
                    }
                }
            }
        }

        /// <inheritdoc/>
        public override void DoOutputEvent(object sender, EventArgs e)
        {
            //  support timers
            if (timers == null || timers.Sum(a => (a.ActivityDue ? 1 : 0)) > 0)
            {
                DoOutput();
            }
        }

    }
}
