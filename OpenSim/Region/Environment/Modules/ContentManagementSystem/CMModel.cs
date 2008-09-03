// CMModel.cs 
// User: bongiojp 
//
//

using System;
using System.Collections.Generic;
using System.Collections;
using libsecondlife;
using OpenSim;
using OpenSim.Framework;
using OpenSim.Region.Environment.Interfaces;
using OpenSim.Region.Environment.Scenes;
using log4net;
using OpenSim.Region.Physics.Manager;
using Axiom.Math;
using System.Diagnostics;

namespace OpenSim.Region.Environment.Modules.ContentManagement
{

    public class CMModel
    {			
        static float TimeToUpdate = 0;
        static float TimeToConvertXml = 0;
	
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        
        IContentDatabase m_database = null;
	
        /// <value>
        /// The class that contains all auras and metaentities used in the CMS.
        /// </value>
        CMEntityCollection m_MetaEntityCollection = new CMEntityCollection();
	
        public CMEntityCollection MetaEntityCollection
        {
            get { return m_MetaEntityCollection; }
        }
	
        public CMModel()
        {
        }
	
        public void Initialise(string database)
        {
            if (database == "FileSystemDatabase")
                m_database = new FileSystemDatabase();
            else if (database == "GitDatabase")
                m_database = new GitDatabase();
        }
	
        public void InitialiseDatabase(Scene scene, string dir)
        {
            m_database.Initialise(scene, dir);
        }
	
        /// <summary>
        /// Should be called just once to finish initializing the database.
        /// </summary>
        public void PostInitialise()
        {
            m_database.PostInitialise();
        }
	
        public ContentManagementEntity FindMetaEntityAffectedByUndo(LLUUID uuid)
        {
            ContentManagementEntity ent = GetMetaGroupByPrim(uuid);
            return ent;
        }
		
        /// <summary>
        /// Removes the green aura when an a new scene object group is deleted.
        /// </summary>
        public void RemoveOrUpdateDeletedEntity(SceneObjectGroup group)
        {
            // Deal with new parts not revisioned that have been deleted.
            foreach(SceneObjectPart part in group.Children.Values)
                if (m_MetaEntityCollection.Auras.ContainsKey(part.UUID))
                    m_MetaEntityCollection.RemoveNewlyCreatedEntityAura(part.UUID);
        }
		
        /// <summary>
        /// Uses the database to serialize all current scene objects into xml and save into a database with an accompanying log message.
        /// </summary>
        public void CommitRegion(Scene scene, String logMessage)
        {
            m_log.Debug("[CONTENT MANAG] saving " + scene.RegionInfo.RegionName + " with log message: " + logMessage + " length of message: " + logMessage.Length);
            m_database.SaveRegion(scene.RegionInfo.RegionID, scene.RegionInfo.RegionName, logMessage);
            m_log.Debug("[CONTENT MANAG] the region name we are dealing with heeeeeeeere: " + scene.RegionInfo.RegionName );
        }
		
        /// <summary>
        /// Retrieves the latest revision of a region in xml form, 
        /// converts it to scene object groups and scene presences, 
        /// swaps the current scene's entity list with the revision's list.
        /// Note: Since deleted objects while 
        /// </summary>
        public void RollbackRegion(Scene scene)
        {
            System.Collections.ArrayList xmllist = null;
            SceneObjectGroup temp = null;
            System.Collections.Hashtable deleteListUUIDs = new Hashtable();
            Dictionary<LLUUID, EntityBase> SearchList = new Dictionary<LLUUID,EntityBase>();
            Dictionary<LLUUID, EntityBase> ReplacementList = new Dictionary<LLUUID,EntityBase>();
            int revision = m_database.GetMostRecentRevision(scene.RegionInfo.RegionID);
            EntityBase[] searchArray;
			
            xmllist = m_database.GetRegionObjectXMLList(scene.RegionInfo.RegionID, revision);
            if (xmllist == null)
            {
                m_log.Info("[CMMODEL]: Region (" + scene.RegionInfo.RegionID + ") does not have given revision number (" + revision + ").");
                return;
            }
			
            m_log.Info("[CMMODEL]: Region (" + scene.RegionInfo.RegionID + ") revision number (" + revision + ").");
            m_log.Info("[CMMODEL]: Scene Objects = " + xmllist.Count);
            m_log.Info("[CMMODEL]: Converting scene entities list to specified revision.");
			
            m_log.ErrorFormat("[CMMODEL]: 1");
			
            foreach (string xml in xmllist)
            {
                try{
                    temp = new SceneObjectGroup(xml);
                    temp.SetScene(scene);
                    foreach(SceneObjectPart part in temp.Children.Values)
                        part.RegionHandle = scene.RegionInfo.RegionHandle;
                    ReplacementList.Add(temp.UUID, (EntityBase)temp);
                } 
                catch(Exception e)
                {
                    m_log.Info("[CMMODEL]: Error while creating replacement list for rollback: " + e);
                }		
            }

            //If in scene but not in revision and not a client, remove them
            while (true)
            {
                try 
                {
                    foreach(EntityBase entity in scene.GetEntities())
                    {
                        if (entity == null)
                            continue;
						
                        if (entity is ScenePresence)
                        {
                            ReplacementList.Add(entity.UUID, entity);
                            continue;
                        }
                        else //if (!ReplacementList.ContainsKey(entity.UUID))
                            deleteListUUIDs.Add(entity.UUID, 0);
                    }
                }
                catch(Exception e)
                {
                    m_log.ErrorFormat("[CMMODEL]: " + e);
                    deleteListUUIDs.Clear();
                    ReplacementList.Clear();
                    continue;
                }
                break;
            }
			
            foreach(LLUUID uuid in deleteListUUIDs.Keys)
            {
                try 
                {
                    // I thought that the DeleteGroup() function would handle all of this, but it doesn't. I'm not sure WHAT it handles.
                    ((SceneObjectGroup)scene.Entities[uuid]).DetachFromBackup((SceneObjectGroup)scene.Entities[uuid]);
                    scene.PhysicsScene.RemovePrim(((SceneObjectGroup)scene.Entities[uuid]).RootPart.PhysActor);
                    scene.SendKillObject(scene.Entities[uuid].LocalId);
                    scene.m_innerScene.DeleteSceneObject(uuid, false);
                    ((SceneObjectGroup)scene.Entities[uuid]).DeleteGroup();
                }
                catch(Exception e)
                {
                    m_log.ErrorFormat("[CMMODEL]: Error while removing objects from scene: " + e);
                }
            }
			
            lock (scene)
            {
                scene.Entities = ReplacementList;
            }

            foreach(EntityBase ent in ReplacementList.Values)
            {
                try
                {	
                    if (!(ent is SceneObjectGroup))
                        continue;
					
                    if ((((SceneObjectGroup)ent).RootPart.GetEffectiveObjectFlags() & (uint) LLObject.ObjectFlags.Phantom) == 0)
                        ((SceneObjectGroup)ent).ApplyPhysics(true);
                    ((SceneObjectGroup)ent).AttachToBackup();
                    ((SceneObjectGroup)ent).HasGroupChanged = true; // If not true, then attaching to backup does nothing because no change is detected.
                    ((SceneObjectGroup)ent).ScheduleGroupForFullUpdate();
                }
                catch(Exception e)
                {
                    m_log.ErrorFormat("[CMMODEL]: Error while attaching new scene entities to backup and scheduling for a full update: " + e);
                }
            }
            m_log.Info("[CMMODEL]: Scheduling a backup of new scene object groups to backup.");
            scene.Backup();
        }
		
        /// <summary>
        /// Detects if a scene object group from the scene list has moved or changed scale. The green aura
        /// that surrounds the object is then moved or scaled with the group.
        /// </summary>
        public System.Collections.ArrayList UpdateNormalEntityEffects(SceneObjectGroup group)
        {
            System.Collections.ArrayList auraList = new System.Collections.ArrayList();
            if (group == null)
                return null;
            foreach(SceneObjectPart part in group.Children.Values)
            {
                if (m_MetaEntityCollection.Auras.ContainsKey(part.UUID))
                {
                    ((AuraMetaEntity)m_MetaEntityCollection.Auras[part.UUID]).SetAura(new LLVector3(0,254,0), part.Scale);
                    ((AuraMetaEntity)m_MetaEntityCollection.Auras[part.UUID]).RootPart.GroupPosition = part.GetWorldPosition();
                    auraList.Add((AuraMetaEntity)m_MetaEntityCollection.Auras[part.UUID]);
                }
            }
            return auraList;
        }
		
        public void DeleteAllMetaObjects()
        {
            m_MetaEntityCollection.ClearAll();
        }

        /// <summary>
        /// Downloads the latest revision of the given scene and converts the xml file to CMEntities. After this method, the view can find the differences
        /// and display the differences to clients.
        /// </summary>
        public void UpdateCMEntities(Scene scene)
        {
            Stopwatch x = new Stopwatch();
            x.Start();

            System.Collections.ArrayList xmllist = null;
            m_log.Debug("[CONTENT MANAGEMENT] Retrieving object xml files for region: " + scene.RegionInfo.RegionID);
            xmllist = m_database.GetRegionObjectXMLList(scene.RegionInfo.RegionID);
            m_log.Info("[FSDB]: got list");
            if (xmllist == null)
                return;

            Stopwatch y = new Stopwatch();
            y.Start();
            foreach (string xml in xmllist)
                m_MetaEntityCollection.CreateNewEntity(xml, scene);
            y.Stop();
            TimeToConvertXml += y.ElapsedMilliseconds;
            m_log.Info("[FileSystemDatabase] Time spent converting xml to metaentities for " + scene.RegionInfo.RegionName + ": " + y.ElapsedMilliseconds);		
            m_log.Info("[FileSystemDatabase] Time spent converting xml to metaentities so far: " + TimeToConvertXml);			
			
            m_log.Info("[FSDB]: checking for new scene object parts missing green auras and create the auras");
            CheckForNewEntitiesMissingAuras(scene);
			
            x.Stop();
            TimeToUpdate += x.ElapsedMilliseconds;
            m_log.Info("[FileSystemDatabase] Time spent Updating entity list for " + scene.RegionInfo.RegionName + ": " + x.ElapsedMilliseconds);		
            m_log.Info("[FileSystemDatabase] Time spent Updating so far: " + TimeToUpdate);
			
        }
		
        /// <summary>
        /// Compares the scene's object group list to the list of meta entities. If there is an object group that does not have a corresponding meta entity
        /// it is a new part that must have a green aura (for diff mode).
        /// Returns list of ContentManagementEntities
        /// </summary>
        public ArrayList CheckForNewEntitiesMissingAuras(Scene scene)
        {	
            ArrayList missingList = null;
            ArrayList newList = new ArrayList();
			
            m_log.Debug("[CONTENT MANAGEMENT] Checking for new scene object parts in scene: " + scene.RegionInfo.RegionName);		
			
            //Check if the current scene has groups not included in the current list of MetaEntities
            //If so, then the current scene's parts that are new should be marked green.		
            missingList = m_MetaEntityCollection.CheckForMissingEntities(scene.GetEntities());

            foreach(Object missingPart in missingList)
            {
                if (m_MetaEntityCollection.Auras.ContainsKey(((SceneObjectPart)missingPart).UUID))
                    continue;
                newList.Add(m_MetaEntityCollection.CreateAuraForNewlyCreatedEntity((SceneObjectPart)missingPart));
            }
            m_log.Info("Number of missing objects found: " + newList.Count);
            return newList;
        }
		
        //-------------------------------- HELPERS --------------------------------------------------------------------//
		
        public ContentManagementEntity GetMetaGroupByPrim(LLUUID uuid)
        {
            foreach (Object ent in m_MetaEntityCollection.Entities.Values)
            {
                if (((ContentManagementEntity)ent).HasChildPrim(uuid))
                    return (ContentManagementEntity)ent;
            }
            return null;
        }
    }	
}