﻿namespace OSAE.Service
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.ServiceModel;
    using System.Threading;
    using System.Timers;
    using System.Net;
    using NetworkCommsDotNet;

    partial class OSAEService
    {
        /// <summary>
        /// Check if there is a OSA message container in the Event log and create one if not.
        /// </summary>
        private void InitialiseOSAInEventLog()
        {
            try
            {
                if (!EventLog.SourceExists("OSAE"))
                    EventLog.CreateEventSource("OSAE", "Application");
            }
            catch (Exception ex)
            {
                logging.AddToLog("CreateEventSource error: " + ex.Message, true);
            }
            this.ServiceName = "OSAE";
            this.EventLog.Source = "OSAE";
            this.EventLog.Log = "Application";
        }        

        private static void DeleteStoreFiles()
        {
            string[] stores = Directory.GetFiles(Common.ApiPath, "*.store", SearchOption.AllDirectories);
            foreach (string f in stores)
                File.Delete(f);
        }       

        /// <summary>
        /// Check if there is an object for the Service running on this machine in OSA, and create one if not.
        /// </summary>
        private void CreateServiceObject()
        {
            try
            {
                logging.AddToLog("Creating Service object", true);
                OSAEObject svcobj = OSAEObjectManager.GetObjectByName("SERVICE-" + Common.ComputerName);
                if (svcobj == null)
                {
                    OSAEObjectManager.ObjectAdd("SERVICE-" + Common.ComputerName, "SERVICE-" + Common.ComputerName, "SERVICE", "", "SYSTEM", true);
                }
                OSAEObjectStateManager.ObjectStateSet("SERVICE-" + Common.ComputerName, "ON", "OSAE Service");
            }
            catch (Exception ex)
            {
                logging.AddToLog("Error creating service object - " + ex.Message, true);
            }
        }                     

        /// <summary>
        /// Starts the various OSA threads that monitors the command Queue, and monitors plugins
        /// </summary>
        private void StartThreads()
        {
            Thread QueryCommandQueueThread = new Thread(new ThreadStart(QueryCommandQueue));
            QueryCommandQueueThread.Start();

            Thread loadPluginsThread = new Thread(new ThreadStart(LoadPlugins));
            loadPluginsThread.Start();

            checkPlugins.Interval = 60000;
            checkPlugins.Enabled = true;
            checkPlugins.Elapsed += new ElapsedEventHandler(checkPlugins_tick);
        }

        /// <summary>
        /// Stops all the plugins
        /// </summary>
        private void ShutDownSystems()
        {             
            logging.AddToLog("Stopping...", true);

            try
            {                          
                checkPlugins.Enabled = false;
                running = false;
                
                logging.AddToLog("Shutting down plugins", true);
                foreach (Plugin p in plugins)
                {                           
                    if (p.Enabled)
                    {
                        logging.AddToLog("Shutting Down Plugin: " + p.PluginName, false);
                        p.Shutdown();
                    }
                }

            }
            catch { }
        }

        /// <summary>
        /// periodically checks the command queue to see if there is any commands that need to be processed by plugins
        /// </summary>        
        private void QueryCommandQueue()
        {
            while (running)
            {
                try
                {
                    foreach (OSAEMethod method in OSAEMethodManager.GetMethodsInQueue())
                    {
                        logging.AddToLog("Method in Queue, ObjectName: " + method.ObjectName + " MethodName: " + method.MethodName, false);

                        LogMethodInformation(method);

                        if (method.ObjectName == "SERVICE-" + Common.ComputerName)
                        {
                            switch (method.MethodName)
                            {
                                case "EXECUTE" : 
                                    logging.AddToLog("Recieved Execute Method Name", true);
                                    UDPConnection.SendObject("Command", method.Parameter1 + " | " + method.Parameter2 + " | " + Common.ComputerName, new IPEndPoint(IPAddress.Broadcast, 10000));
                                    break;
                                case "START PLUGIN":
                                    StartPlugin(method);
                                    break;
                                case "STOP PLUGIN":
                                    StopPlugin(method);
                                    break;
                                case "RELOAD PLUGINS":
                                    LoadPlugins();
                                    break;
                            }                                                                            

                            OSAEMethodManager.MethodQueueDelete(method.Id);
                        }
                        else if (method.ObjectName.Split('-')[0] == "SERVICE")
                        {
                            if(method.MethodName == "EXECUTE")
                                UDPConnection.SendObject("Command", method.Parameter1 + " | " + method.Parameter2 + " | " + method.ObjectName.Substring(8), new IPEndPoint(IPAddress.Broadcast, 10000));

                            OSAEMethodManager.MethodQueueDelete(method.Id);
                        }
                        else
                        {
                            bool processed = false;                            

                            foreach (Plugin plugin in plugins)
                            {
                                if (plugin.Enabled == true && (method.Owner.ToLower() == plugin.PluginName.ToLower() || method.ObjectName.ToLower() == plugin.PluginName.ToLower()))
                                {
                                    logging.AddToLog("Removing method from queue with ID: " + method.Id, false);
                                    plugin.ExecuteCommand(method);
                                    processed = true;
                                    break;
                                }
                            }

                            if (!processed)
                            {
                                UDPConnection.SendObject("Method", method.ObjectName + " | " + method.Owner + " | "
                                    + method.MethodName + " | " + method.Parameter1 + " | " + method.Parameter2 + " | "
                                    + method.Address + " | " + method.Id, new IPEndPoint(IPAddress.Broadcast, 10000));

                                logging.AddToLog("Removing method from queue with ID: " + method.Id, false);
                            }

                            OSAEMethodManager.MethodQueueDelete(method.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logging.AddToLog("Error in QueryCommandQueue: " + ex.Message + " InnerException: " + ex.InnerException, true);
                }

                System.Threading.Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Logs information about a method found in the Queue
        /// </summary>
        /// <param name="method">The method to log</param>
        private void LogMethodInformation(OSAEMethod method)
        {
            logging.AddToLog("Found method in queue: " + method.MethodName, false);
            logging.AddToLog("-- object name: " + method.ObjectName, false);
            logging.AddToLog("-- param 1: " + method.Parameter1, false);
            logging.AddToLog("-- param 2: " + method.Parameter2, false);
            logging.AddToLog("-- object owner: " + method.Owner, false);
        }

        /// <summary>
        /// Stops a plugin based on a method
        /// </summary>
        /// <param name="method">The method containing the information of the plugin to stop</param>
        private void StopPlugin(OSAEMethod method)
        {
            foreach (Plugin p in plugins)
            {
                if (p.PluginName == method.Parameter1)
                {
                    OSAEObject obj = OSAEObjectManager.GetObjectByName(p.PluginName);
                    if (obj != null)
                    {
                        disablePlugin(p);
                        UDPConnection.SendObject("Plugin", p.PluginName + " | " + p.Enabled.ToString() + " | " + p.PluginVersion + " | Stopped | " + p.LatestAvailableVersion + " | " + p.PluginType + " | " + Common.ComputerName, new IPEndPoint(IPAddress.Broadcast, 10000));
                    }
                }
            }
        }

        /// <summary>
        /// Starts a plugin based on a method
        /// </summary>
        /// <param name="method">The method containing the information of the plugin to stop</param>
        private void StartPlugin(OSAEMethod method)
        {
            foreach (Plugin p in plugins)
            {
                if (p.PluginName == method.Parameter1)
                {
                    OSAEObject obj = OSAEObjectManager.GetObjectByName(p.PluginName);
                    if (obj != null)
                    {
                        enablePlugin(p);
                    }
                }
            }
        }        
    }
}
