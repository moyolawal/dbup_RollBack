﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DbUp.Builder;

namespace DbUp.Engine
{
    /// <summary>
    /// This class orchestrates the database upgrade process.
    /// </summary>
    public class UpgradeEngine
    {
        readonly UpgradeConfiguration configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="UpgradeEngine"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        public UpgradeEngine(UpgradeConfiguration configuration)
        {
            this.configuration = configuration;
        }

        /// <summary>
        /// An event that is raised after each script is executed.
        /// </summary>
        public event EventHandler ScriptExecuted;

        /// <summary>
        /// Invokes the <see cref="ScriptExecuted"/> event; called whenever a script is executed.
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnScriptExecuted(ScriptExecutedEventArgs e)
        {
            ScriptExecuted?.Invoke(this, e);
        }

        /// <summary>
        /// Determines whether the database is out of date and can be upgraded.
        /// </summary>
        public bool IsUpgradeRequired()
        {
            return GetScriptsToExecute().Count() != 0;
        }

        /// <summary>
        /// Tries to connect to the database.
        /// </summary>
        /// <param name="errorMessage">Any error message encountered.</param>
        /// <returns></returns>
        public bool TryConnect(out string errorMessage)
        {
            return configuration.ConnectionManager.TryConnect(configuration.Log, out errorMessage);
        }

        /// <summary>
        /// Performs the database upgrade.
        /// </summary>
        public DatabaseUpgradeResult PerformUpgrade()
        {
            var executed = new List<SqlScript>();

            string executedScriptName = null;
            try
            {
                using (configuration.ConnectionManager.OperationStarting(configuration.Log, executed))
                {

                    configuration.Log.WriteInformation("Beginning database upgrade");

                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    if (scriptsToExecute.Count == 0)
                    {
                        configuration.Log.WriteInformation("No new scripts need to be executed - completing.");
                        return new DatabaseUpgradeResult(executed, true, null);
                    }

                    configuration.ScriptExecutor.VerifySchema();

                    foreach (var script in scriptsToExecute)
                    {
                        executedScriptName = script.Name;

                        configuration.ScriptExecutor.Execute(script, configuration.Variables);

                        OnScriptExecuted(new ScriptExecutedEventArgs(script, configuration.ConnectionManager));

                        executed.Add(script);
                    }

                    configuration.Log.WriteInformation("Upgrade successful");
                    return new DatabaseUpgradeResult(executed, true, null);
                }
            }
            catch (Exception ex)
            {
                ex.Data["Error occurred in script: "] = executedScriptName;
                configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                return new DatabaseUpgradeResult(executed, false, ex);
            }
        }

        /// <summary>
        /// Performs the database downgrade.
        /// </summary>
        /// <param name="scriptToRollback">Script to rollback to but not inlcuding itself or the script to rollback
        /// depending on the multipleRollback flag</param>
        /// <param name="rollbackSuffix">Suffix of the rollback scripts</param>
        /// <param name="multipleRollback">True if you want to rollback all scripts up to the given scriptToRollback
        /// but not including it and false if you just want to rollback the given scriptToRollback and nothing else</param>
        /// <returns></returns>
        public DatabaseUpgradeResult PerformDowngrade(string scriptToRollback, string rollbackSuffix, bool multipleRollback)
        {
            var rollbacks = new List<SqlScript>();

            string executedScriptName = null;
            try
            {
                using (configuration.ConnectionManager.OperationStarting(configuration.Log, rollbacks))
                {
                    configuration.Log.WriteInformation("Beginning database downgrade");

                    var scriptsToExecute = GetRollbackScriptsInsideOperation(scriptToRollback, rollbackSuffix, multipleRollback);

                    if (scriptsToExecute == null || scriptsToExecute.Count == 0)
                    {
                        configuration.Log.WriteInformation("No rollback scripts to run {0} - completing.", scriptToRollback);
                        return new DatabaseUpgradeResult(rollbacks, true, null);
                    }

                    configuration.ScriptExecutor.VerifySchema();

                    foreach (var script in scriptsToExecute)
                    {
                        executedScriptName = script.Name;
                        configuration.ScriptExecutor.Execute(script, configuration.Variables);
                        configuration.Journal.RemoveExecutedScript(new SqlScript(executedScriptName.Replace(rollbackSuffix, ""), string.Empty));
                        rollbacks.Add(script);
                    }

                    configuration.Log.WriteInformation("Downgrade successful");
                    return new DatabaseUpgradeResult(rollbacks, true, null);
                }
            }
            catch (Exception ex)
            {
                ex.Data.Add("Error occurred in script: ", executedScriptName);
                configuration.Log.WriteError("Downgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                return new DatabaseUpgradeResult(rollbacks, false, ex);
            }
        }

        /// <summary>
        /// Returns a list of scripts that will be executed when the upgrade is performed
        /// </summary>
        /// <returns>The scripts to be executed</returns>
        public List<SqlScript> GetScriptsToExecute()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return GetScriptsToExecuteInsideOperation();
            }
        }

        public List<string> GetExecutedButNotDiscoveredScripts()
        {
            return GetExecutedScripts().Except(GetDiscoveredScriptsAsEnumerable().Select(x => x.Name)).ToList();
        }

        public List<SqlScript> GetDiscoveredScripts()
        {
            return GetDiscoveredScriptsAsEnumerable().ToList();
        }

        IEnumerable<SqlScript> GetDiscoveredScriptsAsEnumerable()
        {
            return configuration.ScriptProviders.SelectMany(scriptProvider => scriptProvider.GetScripts(configuration.ConnectionManager));
        }

        List<SqlScript> GetScriptsToExecuteInsideOperation()
        {
            var allScripts = GetDiscoveredScriptsAsEnumerable();
            var executedScriptNames = new HashSet<string>(configuration.Journal.GetExecutedScripts());

            var sorted = allScripts.OrderBy(s => s.SqlScriptOptions.RunGroupOrder).ThenBy(s => s.Name, configuration.ScriptNameComparer);
            var filtered = configuration.ScriptFilter.Filter(sorted, executedScriptNames, configuration.ScriptNameComparer);
            return filtered.ToList();
        }


        private List<SqlScript> GetRollbackScriptsInsideOperation(string scriptToRollback, string rollbackSuffix, bool multipleRollback)
        {
            var executedScripts = configuration.Journal.GetExecutedScripts();
            if (!executedScripts.Contains(scriptToRollback))
            {
                configuration.Log.WriteError("Script to rollback cannot be found in the Schema Version table: {0}", scriptToRollback);
                return null;
            }

            var allScripts = configuration.ScriptProviders.SelectMany(scriptProvider => scriptProvider.GetScripts(configuration.ConnectionManager)).ToList();
            var rollbackScripts = new List<SqlScript>();

            if (multipleRollback)
            {
                var rollbackStartingPointPassed = false;
                var rollbackScriptNames = new List<string>();

                foreach (var executedScript in executedScripts)
                {
                    if (rollbackStartingPointPassed)
                    {
                        var rollbackScriptName = Path.GetFileNameWithoutExtension(executedScript) + rollbackSuffix + Path.GetExtension(executedScript);
                        rollbackScriptNames.Add(rollbackScriptName);
                    }
                    else if (executedScript.Equals(scriptToRollback))
                    {
                        rollbackStartingPointPassed = true;
                    }
                }

                // Rollback should be in reverse order
                rollbackScriptNames.Reverse();

                foreach (var rollbackScriptName in rollbackScriptNames)
                {
                    var script = allScripts.SingleOrDefault(x => x.Name.Equals(rollbackScriptName));
                    if (script != null)
                    {
                        rollbackScripts.Add(script);
                    }
                }
            }
            else
            {
                var rollbackScriptName = Path.GetFileNameWithoutExtension(scriptToRollback) + rollbackSuffix + Path.GetExtension(scriptToRollback);
                var script = allScripts.SingleOrDefault(x => x.Name.Equals(rollbackScriptName));
                if (script == null)
                {
                    configuration.Log.WriteWarning("Rollback script cannot be found: {0}", rollbackScriptName);
                }
                else
                {
                    rollbackScripts.Add(script);
                }
            }

            return rollbackScripts;
        }

        public List<string> GetExecutedScripts()
        {
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, new List<SqlScript>()))
            {
                return configuration.Journal.GetExecutedScripts()
                    .ToList();
            }
        }

        ///<summary>
        /// Creates version record for any new migration scripts without executing them.
        /// Useful for bringing development environments into sync with automated environments
        ///</summary>
        ///<returns></returns>
        public DatabaseUpgradeResult MarkAsExecuted()
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.ConnectionManager.ExecuteCommandsWithManagedConnection(
                            connectionFactory => configuration.Journal.StoreExecutedScript(script, connectionFactory));
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }

        public DatabaseUpgradeResult MarkAsExecuted(string latestScript)
        {
            var marked = new List<SqlScript>();
            using (configuration.ConnectionManager.OperationStarting(configuration.Log, marked))
            {
                try
                {
                    var scriptsToExecute = GetScriptsToExecuteInsideOperation();

                    foreach (var script in scriptsToExecute)
                    {
                        configuration.ConnectionManager.ExecuteCommandsWithManagedConnection(
                            commandFactory => configuration.Journal.StoreExecutedScript(script, commandFactory));
                        configuration.Log.WriteInformation("Marking script {0} as executed", script.Name);
                        marked.Add(script);
                        if (script.Name.Equals(latestScript))
                        {
                            break;
                        }
                    }

                    configuration.Log.WriteInformation("Script marking successful");
                    return new DatabaseUpgradeResult(marked, true, null);
                }
                catch (Exception ex)
                {
                    configuration.Log.WriteError("Upgrade failed due to an unexpected exception:\r\n{0}", ex.ToString());
                    return new DatabaseUpgradeResult(marked, false, ex);
                }
            }
        }
    }
}
