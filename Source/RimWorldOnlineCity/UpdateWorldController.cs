﻿using Model;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Transfer;
using Verse;

namespace RimWorldOnlineCity
{
    static class UpdateWorldController
    {
        public static void SendToServer(ModelPlayToServer toServ)
        {
            toServ.LastTick = (long)Find.TickManager.TicksGame;

            //отправка всех новых и измененных объектов игрока
            toServ.WObjects = Find.WorldObjects.AllWorldObjects
                .Where(o => o.Faction != null && o.Faction.IsPlayer && !(o is CaravanOnline) && !(o is BaseOnline))
                .Select(o => GetWorldObjectEntry(o))
                .ToList();

            //свои объекты которые удалил пользователь с последнего обновления
            if (ToDelete != null)
            {
                var toDeleteNewNow = MyWorldObjectEntry
                    .Where(p => !Find.WorldObjects.AllWorldObjects.Any(wo => wo.ID == p.Key))
                    .Select(p => p.Value)
                    .ToList();
                ToDelete.AddRange(toDeleteNewNow);
            }

            toServ.WObjectsToDelete = ToDelete;
        }

        public static void LoadFromServer(ModelPlayToClient fromServ)
        {
            //обновление всех объектов
            ToDelete = new List<WorldObjectEntry>();
            if (fromServ.WObjects != null && fromServ.WObjects.Count > 0)
            {
                for (int i = 0; i < fromServ.WObjects.Count; i++)
                    ApplyWorldObject(fromServ.WObjects[i]);
            }
            if (fromServ.WObjectsToDelete != null && fromServ.WObjectsToDelete.Count > 0)
            {
                for (int i = 0; i < fromServ.WObjectsToDelete.Count; i++)
                    DeleteWorldObject(fromServ.WObjectsToDelete[i]);
            }

            //пришла посылка от каравана другого игрока
            if (fromServ.Mails != null && fromServ.Mails.Count > 0)
            {
                LongEventHandler.QueueLongEvent(delegate
                //LongEventHandler.ExecuteWhenFinished(delegate
                {
                    foreach (var mail in fromServ.Mails)
                    {
                        if (mail.To == null
                            || mail.To.Login != SessionClientController.My.Login
                            || mail.Things == null
                            || mail.Things.Count == 0
                            || mail.PlaceServerId <= 0) continue;
                        //находим наш объект, кому пришла передача
                        var placeId = MyWorldObjectEntry
                            .Where(p => p.Value.ServerId == mail.PlaceServerId)
                            .Select(p => p.Key)
                            .FirstOrDefault();

                        Loger.Log("Mail " + placeId + " "
                            + (mail.From == null ? "-" : mail.From.Login) + "->"
                            + (mail.To == null ? "-" : mail.To.Login) + ":"
                            + mail.ContentString());
                        WorldObject place;
                        if (placeId == 0)
                        {
                            //если нет, и какой-то сбой, посылаем в первый поселек
                            place = Find.WorldObjects.FactionBases
                                .FirstOrDefault(f => f.Faction == Faction.OfPlayer);
                        }
                        else
                        {
                            place = Find.WorldObjects.AllWorldObjects
                                .FirstOrDefault(o => o.ID == placeId && o.Faction == Faction.OfPlayer);
                        }
                        //создаем объекты
                        if (place != null)
                        {
                            DropToWorldObject(place, mail.Things, (mail.From == null ? "-" : mail.From.Login));
                        }
                    }
                }, "", false, null);
            }
        }

        public static void ClearWorld()
        {
            //Loger.Log("ClearWorld");
            var deleteWObjects = Find.WorldObjects.AllWorldObjects
                .Where(o => o is CaravanOnline)
                .ToList();

            for (int i = 0; i < deleteWObjects.Count; i++)
                Find.WorldObjects.Remove(deleteWObjects[i]);
        }

        private static void DropToWorldObject(WorldObject place, List<ThingEntry> things, string from)
        {
            GlobalTargetInfo ti = new GlobalTargetInfo(place);
            if (place is FactionBase && ((FactionBase)place).Map != null)
            {
                var cell = GameUtils.GetTradeCell(((FactionBase)place).Map);
                ti = new GlobalTargetInfo(cell, ((FactionBase)place).Map);
                Thing thinXZ;
                foreach (var thing in things)
                {
                    var thin = thing.CreateThing(false);
                    var map = ((FactionBase)place).Map;
                    if (thin is Pawn)
                        GenSpawn.Spawn((Pawn)thin, cell, map);
                    else
                        GenDrop.TryDropSpawn(thin, cell, map, ThingPlaceMode.Near, out thinXZ, null);
                }
            }
            else if (place is Caravan)
            {
                var pawns = (place as Caravan).PawnsListForReading;
                foreach (var thing in things)
                {
                    var thin = thing.CreateThing(false);
                    if (thin is Pawn)
                    {
                        (place as Caravan).AddPawn(thin as Pawn, true);
                        GameUtils.SpawnSetupOnCaravan(thin as Pawn);
                    }
                    else
                    {
                        var p = CaravanInventoryUtility.FindPawnToMoveInventoryTo(thin, pawns, null);
                        if (p != null)
                            p.inventory.innerContainer.TryAdd(thin, true);
                    }
                }
            }
            Find.LetterStack.ReceiveLetter("OCity_UpdateWorld_Trade".Translate()
                , string.Format("OCity_UpdateWorld_TradeDetails".Translate()
                    , from
                    , place.LabelCap
                    , things.Aggregate("", (r, i) => r + Environment.NewLine + i.Name + " x" + i.Count))
                , LetterDefOf.PositiveEvent
                , ti
                , null);
        }

        #region WorldObject

        /// <summary>
        /// Для поиска объектов, уже созданных в прошлые разы
        /// </summary>
        private static Dictionary<long, int> ConverterServerId;
        private static Dictionary<int, WorldObjectEntry> MyWorldObjectEntry;
        private static List<WorldObjectEntry> ToDelete;

        /// <summary>
        /// Только для своих объетков
        /// </summary>
        public static WorldObjectEntry GetWorldObjectEntry(WorldObject worldObject)
        {
            var worldObjectEntry = new WorldObjectEntry();
            worldObjectEntry.Type = worldObject is Caravan ? WorldObjectEntryType.Caravan : WorldObjectEntryType.Base;
            worldObjectEntry.Tile = worldObject.Tile;
            worldObjectEntry.Name = worldObject.LabelCap;
            worldObjectEntry.LoginOwner = SessionClientController.My.Login;
            worldObjectEntry.FreeWeight = 999999;

            //определяем цену и вес 
            var caravan = worldObject as Caravan;
            if (caravan != null)
            {
                var transferables = CalculateTransferables(caravan);

                List<ThingStackPart> stackParts = new List<ThingStackPart>();
                for (int i = 0; i < transferables.Count; i++)
                {
                    TransferableUtility.TransferNoSplit(transferables[i].things, transferables[i].MaxCount/*CountToTransfer*/, delegate (Thing originalThing, int toTake)
                    {
                        stackParts.Add(new ThingStackPart(originalThing, toTake));
                    }, false, false);
                }
                worldObjectEntry.FreeWeight = CollectionsMassCalculator.Capacity(stackParts)
                    - CollectionsMassCalculator.MassUsage(stackParts, IgnorePawnsInventoryMode.Ignore, false, false);

                worldObjectEntry.MarketValue = 0f;
                worldObjectEntry.MarketValuePawn = 0f;
                for (int i = 0; i < stackParts.Count; i++)
                {
                    int count = stackParts[i].Count;

                    if (count > 0)
                    {
                        Thing thing = stackParts[i].Thing;
                        if (thing is Pawn)
                        {
                            worldObjectEntry.MarketValuePawn += thing.MarketValue
                                + WealthWatcher.GetEquipmentApparelAndInventoryWealth(thing as Pawn);
                        }
                        else
                            worldObjectEntry.MarketValue += thing.MarketValue * (float)count;
                    }
                }
            }
            else if (worldObject is FactionBase)
            {
                var map = (worldObject as FactionBase).Map;
                if (map != null)
                {
                    worldObjectEntry.MarketValue = map.wealthWatcher.WealthTotal;

                    worldObjectEntry.MarketValuePawn = 0;
                    foreach (Pawn current in map.mapPawns.FreeColonists)
                    {
                        worldObjectEntry.MarketValuePawn += current.MarketValue;
                    }
                    //Loger.Log("Map things "+ worldObjectEntry.MarketValue + " pawns " + worldObjectEntry.MarketValuePawn);
                }
            }
            
            WorldObjectEntry storeWO;
            if (MyWorldObjectEntry.TryGetValue(worldObject.ID, out storeWO))
            {
                //если серверу приходит объект без данного ServerId, значит это наш новый объект (кроме первого запроса, т.к. не было ещё загрузки)
                worldObjectEntry.ServerId = storeWO.ServerId;
            }

            return worldObjectEntry;
        }


        #region Вычисление массы, сдернуто с Dialog_SplitCaravan

        private static List<TransferableOneWay> CalculateTransferables(Caravan caravan)
        {
            var transferables = new List<TransferableOneWay>();
            AddPawnsToTransferables(caravan, transferables);
            AddItemsToTransferables(caravan, transferables);
            return transferables;
        }

        private static void AddPawnsToTransferables(Caravan caravan, List<TransferableOneWay> transferables)
        {
            List<Pawn> pawnsListForReading = caravan.PawnsListForReading;
            for (int i = 0; i < pawnsListForReading.Count; i++)
            {
                AddToTransferables(pawnsListForReading[i], transferables);
            }
        }

        private static void AddItemsToTransferables(Caravan caravan, List<TransferableOneWay> transferables)
        {
            List<Thing> list = CaravanInventoryUtility.AllInventoryItems(caravan);
            for (int i = 0; i < list.Count; i++)
            {
                AddToTransferables(list[i], transferables);
            }
        }

        private static void AddToTransferables(Thing t, List<TransferableOneWay> transferables)
        {
            TransferableOneWay transferableOneWay = TransferableUtility.TransferableMatching<TransferableOneWay>(t, transferables);
            if (transferableOneWay == null)
            {
                transferableOneWay = new TransferableOneWay();
                transferables.Add(transferableOneWay);
            }
            transferableOneWay.things.Add(t);
        }
        #endregion 
        
        /// <summary>
        /// Для всех объектов с сервера, в т.ч. и для наших.
        /// Для своих объектов заполняем данные в словарь MyWorldObjectEntry
        /// </summary>
        /// <param name="worldObjectEntry"></param>
        /// <returns></returns>
        public static void ApplyWorldObject(WorldObjectEntry worldObjectEntry)
        {
            List<WorldObject> allWorldObjects = Find.WorldObjects.AllWorldObjects;

            if (worldObjectEntry.LoginOwner == SessionClientController.My.Login)
            {
                //для своих нужно только занести в MyWorldObjectEntry (чтобы запомнить ServerId)
                if (MyWorldObjectEntry.Any(wo => wo.Value.ServerId == worldObjectEntry.ServerId))
                    return;

                for (int i = 0; i < allWorldObjects.Count; i++)
                {
                    if (!MyWorldObjectEntry.ContainsKey(allWorldObjects[i].ID)
                        && allWorldObjects[i].Tile == worldObjectEntry.Tile 
                        && (allWorldObjects[i] is Caravan && worldObjectEntry.Type == WorldObjectEntryType.Caravan
                            || allWorldObjects[i] is FactionBase && worldObjectEntry.Type == WorldObjectEntryType.Base))
                    {
                        Loger.Log("SetMyID " + allWorldObjects[i].ID + " ServerId " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);
                        MyWorldObjectEntry.Add(allWorldObjects[i].ID, worldObjectEntry);
                        return;
                    }
                }

                Loger.Log("ToDel " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);

                //объект нужно удалить на сервере - его нету у самого игрока (не заполняется при самом первом обновлении после загрузки)
                if (ToDelete != null) ToDelete.Add(worldObjectEntry);
                return;
            }
            
            //поиск уже существующих
            CaravanOnline worldObject = null;
            int existId;
            if (ConverterServerId.TryGetValue(worldObjectEntry.ServerId, out existId))
            {
                for (int i = 0; i < allWorldObjects.Count; i++)
                {
                    if (allWorldObjects[i].ID == existId && allWorldObjects[i] is CaravanOnline)
                    {
                        worldObject = allWorldObjects[i] as CaravanOnline;
                        break;
                    }
                }
            }

            //если тут база другого игрока, то удаление всех кто занимает этот тайл, кроме караванов (удаление новых НПЦ и событий с занятых тайлов)
            if (worldObjectEntry.Type == WorldObjectEntryType.Base)
            {
                for (int i = 0; i < allWorldObjects.Count; i++)
                {
                    if (allWorldObjects[i].Tile == worldObjectEntry.Tile && allWorldObjects[i] != worldObject
                        && !(allWorldObjects[i] is Caravan) && !(allWorldObjects[i] is CaravanOnline))
                    {
                        Loger.Log("Remove " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);
                        Find.WorldObjects.Remove(allWorldObjects[i]);
                    }
                }
            }

            //создание
            if (worldObject == null)
            {
                worldObject = worldObjectEntry.Type == WorldObjectEntryType.Base
                    ? (CaravanOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.BaseOnline)
                    : (CaravanOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.CaravanOnline);
                worldObject.SetFaction(Faction.OfPlayer);
                worldObject.Tile = worldObjectEntry.Tile;
                Find.WorldObjects.Add(worldObject);
                ConverterServerId.Add(worldObjectEntry.ServerId, worldObject.ID);
                Loger.Log("Add " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name + " " + worldObjectEntry.LoginOwner);
            }
            else
            {
                ConverterServerId[worldObjectEntry.ServerId] = worldObject.ID; //на всякий случай
                Loger.Log("SetID " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);
            }
            //обновление
            worldObject.Tile = worldObjectEntry.Tile;
            worldObject.OnlineWObject = worldObjectEntry;
            
        }

        public static void DeleteWorldObject(WorldObjectEntry worldObjectEntry)
        {
            List<WorldObject> allWorldObjects = Find.WorldObjects.AllWorldObjects;
            
            //поиск уже существующих
            CaravanOnline worldObject = null;
            int existId;
            if (ConverterServerId.TryGetValue(worldObjectEntry.ServerId, out existId))
            {
                for (int i = 0; i < allWorldObjects.Count; i++)
                {
                    if (allWorldObjects[i].ID == existId && allWorldObjects[i] is CaravanOnline)
                    {
                        worldObject = allWorldObjects[i] as CaravanOnline;
                        break;
                    }
                }
            }
            Loger.Log("DeleteWorldObject " + DevelopTest.TextObj(worldObjectEntry) + " " + existId + " " 
                + (worldObject == null ? "null" : worldObject.ID.ToString()));

            if (worldObject != null)
            {
                Find.WorldObjects.Remove(worldObject);
            }
        }
        public static void InitGame()
        {
            MyWorldObjectEntry = new Dictionary<int, WorldObjectEntry>();
            ConverterServerId = new Dictionary<long, int>();
            ToDelete = null;
        }

        #endregion
    }
}
