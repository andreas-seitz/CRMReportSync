using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using XrmToolBox.Extensibility;


using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;
using System.IO;
using XrmToolBox.Extensibility.Interfaces;
using XrmToolBox.Extensibility.Args;

namespace SeAn.XrmToolboxPlugins
{
    public partial class CrmReportSyncControl : PluginControlBase
    {
        public CrmReportSyncControl()
        {
            InitializeComponent();
            
            txtPath.Text = GetPath();
        }

        public void CheckReports()
        {
            if (string.IsNullOrEmpty(GetPath()))
            {
                MessageBox.Show("Please choose path to reports", "Path is missing");
                return;
            }

            if (Service == null)
            {
                MessageBox.Show("Please connect first", "no connection");
                return;
            }
            gridReports.Rows.Clear();
            EnableControls(false);
            WorkAsync(new WorkAsyncInfo
            {
                Message = "loading report-info...",
                Work = (w, e) =>
                {
                    e.Result = new CheckReportsResult()
                    {
                        LokalReportList = Directory.GetFiles(GetPath(), "*" + txtFileFilter.Text + "*.rdl").ToList(),
                        CrmReportList = GetReportListFromCRM(Service).ToList()
                    };           
                },
                ProgressChanged = e =>
                {
                    SetWorkingMessage("Loading Reports...");
                },
                PostWorkCallBack = e =>
                {
                    var res = (CheckReportsResult)e.Result;
                    var fileNameCheck = new List<string>();

                    foreach (var reportFile in res.LokalReportList)
                    {
                        var fileInfo = new FileInfo(reportFile);
                        fileNameCheck.Add(fileInfo.Name.ToLower());

                        var crmReport = res.CrmReportList.Where(t => t.GetAttributeValue<string>("filename").Equals(fileInfo.Name, StringComparison.InvariantCultureIgnoreCase)).ToList();

                        var row = gridReports.Rows[gridReports.Rows.Add()];
                        row.Cells["Dateiname"].Value = fileInfo.Name;
                        row.Cells["Pfad"].Value = fileInfo.FullName;
                        row.Cells["Status"].Value = "ok";
                        row.Cells["Status"].Style.ForeColor = Color.Black;

                        if (crmReport.Count > 0)
                        {
                            row.Cells["Id"].Value = crmReport[0].Id;
                            if (crmReport.Count > 1)
                            {
                                // more than one report with same filename found -> to do: show info for user or update all duplicates
                            }

                            var crmReportSelected = crmReport[0]; //use first one

                            if (fileInfo.LastWriteTime > crmReportSelected.GetAttributeValue<DateTime>("modifiedon").ToLocalTime())
                            {
                                row.Cells["Status"].Value = "modifieddate later than crm";
                                row.Cells["Status"].Style.ForeColor = Color.Red;
                            }
                        }
                        else
                        {
                            row.Cells["Status"].Value = "on disk, but not in CRM";
                            row.Cells["Status"].Style.ForeColor = Color.Red;
                        }
                    }

                    if (chkCRM.Checked)
                    {
                        foreach (var reportFile in res.CrmReportList)
                        {
                            string filename = reportFile.GetAttributeValue<string>("filename");

                            if (fileNameCheck.Contains(filename.ToLower())) continue;

                            var row = gridReports.Rows[gridReports.Rows.Add()];
                            row.Cells["Dateiname"].Value = filename;
                            row.Cells["Id"].Value = reportFile.GetAttributeValue<Guid>("reportid");

                            row.Cells["Status"].Value = "in CRM, but not on disk";
                            row.Cells["Status"].Style.ForeColor = Color.Red;
                        }
                    }

                    gridReports.Sort(gridReports.Columns["Dateiname"], ListSortDirection.Ascending);

                    EnableControls(true);
                },
                AsyncArgument = null,
                IsCancelable = true,
                MessageWidth = 340,
                MessageHeight = 150
            });
        }


        private void DownloadReports()
        {
            if (Service == null)
            {
                MessageBox.Show("Please connect first", "no connection");
                return;
            }
            EnableControls(false);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "download reports...",
                Work = (w, e) =>
                {
                    var idLIst = new List<Guid>();
                    foreach (DataGridViewRow row in gridReports.Rows)
                    {
                        if (row.Cells["Aktualisieren"].Value != null && (bool)row.Cells["Aktualisieren"].Value == true && row.Cells["Pfad"].Value == null)
                        {
                            idLIst.Add((Guid)row.Cells["Id"].Value);
                        }
                    }
                    var reports = GetReportsFromCRM(Service, idLIst);

                    foreach(var reportItem in reports)
                    {
                        if(reportItem.Contains("bodytext"))
                        {
                            string path = GetPath() + @"\" + reportItem.GetAttributeValue<string>("filename");
                            File.WriteAllText(path, reportItem.GetAttributeValue<string>("bodytext"));
                            File.SetLastWriteTime(path, reportItem.GetAttributeValue<DateTime>("modifiedon").ToLocalTime());
                        }
                    }
                },
                ProgressChanged = e =>
                {
                    SetWorkingMessage("download reports...");
                },
                PostWorkCallBack = e =>
                {
                    UploadReports();
                },
                AsyncArgument = null,
                IsCancelable = true,
                MessageWidth = 340,
                MessageHeight = 150
            });
        }

        private void UploadReports()
        {
            if (Service == null)
            {
                MessageBox.Show("Please connect first", "no connection");
                return;
            }

            EnableControls(false);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "upload reports...",
                Work = (w, e) =>
                {
                    var req = new ExecuteMultipleRequest()
                    {
                        Settings = new ExecuteMultipleSettings()
                        {
                            ContinueOnError = true,
                            ReturnResponses = true
                        },
                        Requests = new OrganizationRequestCollection()
                    };

                    var entUpdateList = new List<Entity>();
                    foreach (DataGridViewRow row in gridReports.Rows)
                    {
                        if (row.Cells["Aktualisieren"].Value != null && (bool)row.Cells["Aktualisieren"].Value == true && row.Cells["Pfad"].Value != null)
                        {
                            var reportUpdate = new Entity("report");
                            reportUpdate["bodytext"] = File.ReadAllText((string)row.Cells["Pfad"].Value);

                            if (row.Cells["Id"] != null && row.Cells["Id"].Value != null)
                            {
                                reportUpdate.Id = (Guid)row.Cells["Id"].Value;
                            }
                            else
                            {
                                // to do: set parameters for create
                                // this active version supports only updating existing reports -> have to find out which parameter must be set for create
                            }

                            entUpdateList.Add(reportUpdate);
                        }
                    }

                    if (entUpdateList.Count > 0)
                    {
                        e.Result = UpdateOrCreateEntities(Service, entUpdateList);
                    }
                },
                ProgressChanged = e =>
                {
                    SetWorkingMessage("Upload Reports...");
                },
                PostWorkCallBack = e =>
                {
                    CheckReports();
                    if (e.Result != null)
                    {
                        var res = (List<UpdateOrCreateEntitiesResult>)e.Result;

                        foreach (var resItem in res)
                        {
                            var id = Guid.Empty;
                            if (resItem.Request.GetType() == typeof(CreateRequest))
                            {
                                id = ((CreateRequest)resItem.Request).Target.Id;
                            }
                            else if (resItem.Request.GetType() == typeof(UpdateRequest))
                            {
                                id = ((UpdateRequest)resItem.Request).Target.Id;
                            }

                            foreach (DataGridViewRow row in gridReports.Rows)
                            {
                                if (row.Cells["Id"] != null && row.Cells["Id"].Value != null && (Guid)row.Cells["Id"].Value == id)
                                {
                                    row.Cells["Status"].Value = resItem.Fault.Message;
                                    row.Cells["Status"].Style.ForeColor = Color.Red;
                                }
                            }
                        }
                    }
                    EnableControls(true);
                },
                AsyncArgument = null,
                IsCancelable = true,
                MessageWidth = 340,
                MessageHeight = 150
            });
        }

        #region helper

        public class CheckReportsResult
        {
            public List<Entity> CrmReportList { get; set; }
            public List<string> LokalReportList { get; set; }
        }

        public class UpdateOrCreateEntitiesResult
        {
            public OrganizationServiceFault Fault { get; set; }
            public OrganizationRequest Request { get; set; }

        }

        public static List<UpdateOrCreateEntitiesResult> UpdateOrCreateEntities(IOrganizationService crmService, List<Entity> entities)
        {
            var faults = new List<UpdateOrCreateEntitiesResult>();
            int size = 500;

            do
            {
                var req = new ExecuteMultipleRequest();
                req.Settings = new ExecuteMultipleSettings() { ContinueOnError = false, ReturnResponses = true };
                req.Requests = new OrganizationRequestCollection();

                foreach (Entity updEntity in entities.Take(size))
                {
                    if (Guid.Empty.Equals(updEntity.Id)) req.Requests.Add(new CreateRequest() { Target = updEntity });
                    else req.Requests.Add(new UpdateRequest() { Target = updEntity });
                }

                var resp = (ExecuteMultipleResponse)crmService.Execute(req);

                foreach (var fault in resp.Responses.Where(t => t.Fault != null))
                {
                    faults.Add(new UpdateOrCreateEntitiesResult() { Fault = fault.Fault, Request = req.Requests[fault.RequestIndex] });
                }
                entities.RemoveRange(0, entities.Count < size ? entities.Count : size);
            }
            while (entities.Count > 0);
            return faults;
        }

        private DataCollection<Entity> GetReportListFromCRM(IOrganizationService crmService)
        {
            var query = new QueryExpression("report")
            {
                ColumnSet = new ColumnSet("name", "filename", "reportidunique", "iscustomreport", "description", "filesize", "modifiedon", "reportid"),
                //Criteria = new FilterExpression()
                //{
                //Conditions =
                //  {
                //      new ConditionExpression("iscustomreport", ConditionOperator.Equal, true)
                //  }
                //}
            };

            if (txtFileFilter.Text.Length>0)
            {
                query.Criteria = new FilterExpression(LogicalOperator.And);
                query.Criteria.AddCondition("filename", ConditionOperator.Like,string.Format("%{0}%", txtFileFilter.Text.Replace("*","%")));
            }

            return crmService.RetrieveMultiple(query).Entities;
        }

        private Entity GetReporFromCRM(IOrganizationService crmService, Guid id)
        {
            return crmService.Retrieve("report", id, new ColumnSet("name", "filename", "reportidunique", "iscustomreport", "description", "filesize", "modifiedon", "reportid", "bodytext"));
        }

        private DataCollection<Entity> GetReportsFromCRM(IOrganizationService crmService, List<Guid> ids)
        {
            var currentReports = crmService.RetrieveMultiple(new QueryExpression("report")
            {
                ColumnSet = new ColumnSet("name", "filename", "reportidunique", "iscustomreport", "description", "filesize", "modifiedon", "reportid", "bodytext"),
                Criteria = new FilterExpression()
                {
                    Conditions =
                         {
                              new ConditionExpression("reportid", ConditionOperator.In, ids.ToArray())
                         }
                }
            }).Entities;
            return currentReports;
        }

        private string GetPath()
        {
            return XrmToolbox.Plugins.Properties.Settings.Default.Path;
        }

        private void EnableControls(bool enable)
        {
            gridReports.Enabled = btnCheck.Enabled = btnUpdate.Enabled = btnChangePath.Enabled = chkCRM.Enabled = chkToggle.Enabled = enable;

        }
        #endregion

        #region events
        private void btnChangePath_Click(object sender, EventArgs e)
        {
            var folderBrowserDialog = new FolderBrowserDialog
            {
                Description = "Select a folder containing the .RDL Files you wish to download or upload",
                ShowNewFolderButton = true
            };

            if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
            {
                txtPath.Text = XrmToolbox.Plugins.Properties.Settings.Default.Path = folderBrowserDialog.SelectedPath;
                XrmToolbox.Plugins.Properties.Settings.Default.Save();
                CheckReports();
            }
        }

        private void btnCheck_Click(object sender, EventArgs e)
        {
            CheckReports();
        }

        private void btnUpdate_Click(object sender, EventArgs e)
        {
            DownloadReports(); 
        }

        private void chkToggle_CheckedChanged(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in gridReports.Rows)
            {
                row.Cells["Aktualisieren"].Value = chkToggle.Checked;
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            CancelWorker(); 

            MessageBox.Show("Cancelled");
        }
        #endregion
    }
}
