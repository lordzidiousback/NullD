﻿/*
 * Copyright (C) 2011 - 2018 NullD project
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using NullD.Common.Logging;
using NullD.Common.Storage.AccountDataBase.Entities;
using NullD.Core.GS.Items;
using NullD.Net.GS;
using NullD.Net.GS.Message;
using NullD.Net.GS.Message.Definitions.Inventory;
using NullD.Net.GS.Message.Fields;
using NullD.Net.GS.Message.Definitions.ACD;
using NullD.Core.GS.Common;
using NullD.Common.MPQ.FileFormats;
using NullD.Common.Storage;
using NullD.Net.GS.Message.Definitions.Stash;
using NullD.Core.GS.Objects;
using NullD.Core.LogNet.Toons;
using NHibernate.Linq;

namespace NullD.Core.GS.Players
{

    public class Inventory : IMessageConsumer, IRevealable
    {

        static readonly Logger Logger = LogManager.CreateLogger();

        // Access by ID
        private readonly Player _owner; // Used, because most information is not in the item class but Actors managed by the world

        //Values for buying new slots on stash
        private readonly int[] _stashBuyValue = { 100000, 200000 }; // from Pacth 13, stash is limited to 3 tabs

        public bool Loaded { get; private set; }
        private Equipment _equipment;
        private InventoryGrid _inventoryGrid;
        private InventoryGrid _stashGrid;
        private Item _inventoryGold;
        // backpack for spellRunes, their Items are kept in equipment
        private uint[] _skillSocketRunes;


        public Inventory(Player owner)
        {
            this._owner = owner;
            this._equipment = new Equipment(owner);
            this._inventoryGrid = new InventoryGrid(owner, owner.Attributes[GameAttribute.Backpack_Slots] / 10, 10);
            this._stashGrid = new InventoryGrid(owner, owner.Attributes[GameAttribute.Shared_Stash_Slots] / 7, 7, (int)EquipmentSlotId.Stash);
            this._skillSocketRunes = new uint[6];
        }

        private void AcceptMoveRequest(Item item)
        {
            /* _owner.InGameClient.SendMessage(new ACDInventoryPositionMessage()
             {
                 ItemId = item.DynamicID,
                 InventoryLocation = item.InventoryLocationMessage,
                 Field2 = 1 // what does this do?  // 0 - source item not disappearing from inventory, 1 - Moving, any other possibilities? its an int32
             }); */
        }
        public List<Item> GetBackPackItems()
        {
            return new List<Item>(this._inventoryGrid.Items.Values);
        }


        public List<Item> GetEquippedItems()
        {
            return this._equipment.Items.Values.ToList();
        }

        /// <summary>
        /// Refreshes the visual appearance of the hero
        /// </summary>
        public void SendVisualInventory(Player player)
        {
            var message = new VisualInventoryMessage()
                              {
                                  ActorID = this._owner.DynamicID,
                                  EquipmentList = new VisualEquipment()
                                                      {
                                                          Equipment = this._equipment.GetVisualEquipment()
                                                      },
                              };

            //player.InGameClient.SendMessage(message);
            player.World.BroadcastGlobal(message);
        }

        public D3.Hero.VisualEquipment GetVisualEquipment()
        {
            return this._equipment.GetVisualEquipmentForToon();
        }

        public bool HasInventorySpace(Item item)
        {
            return _inventoryGrid.HasFreeSpace(item);
        }

        public void SpawnItem(Item item)
        {
            _inventoryGrid.AddItem(item);
        }

        /// <summary>
        /// Picks an item up after client request
        /// </summary>
        /// <returns>true if the item was picked up, or false if the player could not pick up the item.</returns>
        public bool PickUp(Item item)
        {
            System.Diagnostics.Debug.Assert(!_inventoryGrid.Contains(item) && !_equipment.IsItemEquipped(item), "Item already in inventory");
            // TODO: Autoequip when equipment slot is empty

            // If Item is Stackable try to add the amount
            if (item.IsStackable())
            {

                // Find items of same type (GBID) and try to add it to one of them
                List<Item> baseItems = FindSameItems(item.GBHandle.GBID);
                foreach (Item baseItem in baseItems)
                {
                    int MaxAmount = baseItem.ItemDefinition.MaxStackAmount;
                    if (baseItem.ActorSNO.Id == 5656)
                        MaxAmount = 20;
                    if (baseItem.Attributes[GameAttribute.ItemStackQuantityLo] + item.Attributes[GameAttribute.ItemStackQuantityLo] < MaxAmount)
                    {
                        baseItem.Attributes[GameAttribute.ItemStackQuantityLo] += item.Attributes[GameAttribute.ItemStackQuantityLo];
                        baseItem.Attributes.SendChangedMessage(_owner.InGameClient);

                        // Item amount successful added. Don't place item in inventory instead destroy it.
                        item.Destroy();
                        return true;
                    }
                }
            }

            bool success = false;
            if (!_inventoryGrid.HasFreeSpace(item))
            {
                // Inventory full
                _owner.InGameClient.SendMessage(new ACDPickupFailedMessage()
                {
                    ItemID = item.DynamicID,
                    Reason = ACDPickupFailedMessage.Reasons.InventoryFull
                });
            }
            else
            {
                item.CurrentState = ItemState.PickingUp;
                if (item.HasWorldLocation && item.World != null)
                {
                    item.Owner = _owner;
                    item.World.Leave(item);
                }

                _inventoryGrid.AddItem(item);

                if (_owner.GroundItems.ContainsKey(item.DynamicID))
                    _owner.GroundItems.Remove(item.DynamicID);
                success = true;
                item.CurrentState = ItemState.Normal;
                AcceptMoveRequest(item);
            }

            return success;
        }

        public bool ToSold(Item item)
        {
            //3384843174
            System.Diagnostics.Debug.Assert(!_inventoryGrid.Contains(item) && !_equipment.IsItemEquipped(item), "Item already in inventory");
            // TODO: Autoequip when equipment slot is empty



            // If Item is Stackable try to add the amount
            if (item.IsStackable())
            {
                // Find items of same type (GBID) and try to add it to one of them
                List<Item> baseItems = FindSameItems(item.GBHandle.GBID);
                foreach (Item baseItem in baseItems)
                {
                    int MaxAmount = baseItem.ItemDefinition.MaxStackAmount;
                    if (baseItem.ActorSNO.Id == 5656)
                        MaxAmount = 20;
                    if (baseItem.Attributes[GameAttribute.ItemStackQuantityLo] + item.Attributes[GameAttribute.ItemStackQuantityLo] < MaxAmount)
                    {
                        baseItem.Attributes[GameAttribute.ItemStackQuantityLo] += item.Attributes[GameAttribute.ItemStackQuantityLo];
                        baseItem.Attributes.SendChangedMessage(_owner.InGameClient);

                        // Item amount successful added. Don't place item in inventory instead destroy it.
                        item.Destroy();
                        return true;
                    }
                }
            }

            bool success = false;
            if (!_inventoryGrid.HasFreeSpace(item))
            {
                // Inventory full
                _owner.InGameClient.SendMessage(new ACDPickupFailedMessage()
                {
                    ItemID = item.DynamicID,
                    Reason = ACDPickupFailedMessage.Reasons.InventoryFull
                });
            }
            else
            {
                item.CurrentState = ItemState.PickingUp;
                if (item.HasWorldLocation && item.World != null)
                {
                    item.Owner = _owner;
                    item.World.Leave(item);
                }

                _inventoryGrid.AddItem(item);

                if (_owner.GroundItems.ContainsKey(item.DynamicID))
                    _owner.GroundItems.Remove(item.DynamicID);
                success = true;
                item.CurrentState = ItemState.Normal;

                AcceptMoveRequest(item);
            }

            return success;
        }
        /// <summary>
        /// Used for equiping item after game starts
        /// TOOD: Needs rewrite
        /// </summary>
        /// <param name="item"></param>
        /// <param name="slot"></param>
        public void EquipItem(Item item, int slot)
        {
            this._equipment.EquipItem(item, slot);
        }

        private List<Item> FindSameItems(int gbid)
        {
            return _inventoryGrid.Items.Values.Where(i => i.GBHandle.GBID == gbid).ToList();
        }

        public void BuyItem(Item originalItem)
        {
            // TODO: Create a copy instead of random.

            var newItem = ItemGenerator.CloneItem(originalItem);
            newItem.Quality = originalItem.Quality;
            //if(newItem.Is)
            if (Item.IsArmor(newItem.ItemType))
            {
                newItem.Attributes[GameAttribute.Armor_Item] = originalItem.Attributes[GameAttribute.Armor_Item];
                newItem.AffixList = originalItem.AffixList;


            }
            else if (Item.IsWeapon(newItem.ItemType))
            {
                newItem.Attributes[GameAttribute.Attacks_Per_Second_Item] = originalItem.Attributes[GameAttribute.Attacks_Per_Second_Item];
                newItem.Attributes[GameAttribute.Damage_Weapon_Min, 0] = originalItem.Attributes[GameAttribute.Damage_Weapon_Min, 0];
                newItem.Attributes[GameAttribute.Damage_Weapon_Delta, 0] = originalItem.Attributes[GameAttribute.Damage_Weapon_Delta, 0];
                newItem.AffixList = originalItem.AffixList;
            }

            if (originalItem.ItemDefinition.MaxStackAmount > 1)
            {
                var ItemDef = Items.ItemGenerator.GetItemDefinition(originalItem.ItemDefinition.Hash);
                Item DirectItem = null;
                foreach (var item in this.GetBackPackItems())
                {
                    if (item.ActorSNO.Id == ItemDef.SNOActor & item.ItemDefinition.MaxStackAmount != item.Attributes[GameAttribute.ItemStackQuantityLo])
                    {
                        DirectItem = item;
                    }
                }
                if (DirectItem != null)
                {
                    DirectItem.Attributes[GameAttribute.ItemStackQuantityLo]++;
                    DirectItem.Attributes.SendChangedMessage(_owner.InGameClient);
                }
                else
                {
                    _inventoryGrid.AddItem(newItem);
                }

            }
            else
            {

                newItem.Attributes[GameAttribute.Durability_Cur] = originalItem.Attributes[GameAttribute.Durability_Cur];
                newItem.Attributes[GameAttribute.Durability_Max] = originalItem.Attributes[GameAttribute.Durability_Cur];
                _inventoryGrid.AddItem(newItem);
            }
        }

        public void SellItem(Item item)
        {
            if (_equipment.IsItemEquipped(item))
            {
                _equipment.UnequipItem(item);
                SendVisualInventory(_owner);
            }
            else
            {
                _inventoryGrid.RemoveItem(item);
                SendVisualInventory(_owner);
            }
            DestroyInventoryItem(item);
            //_inventoryGrid.AddSelledItem(item);
            //_destroyedItems.Add(item);
            //SaveToDB();
            //AcceptMoveRequest(item);
            SendVisualInventory(_owner);

            RefreshInventoryToClient();

        }

        /// <summary>
        /// Handles a request to move an item within the inventory.
        /// This covers moving items within the backpack, from equipment
        /// slot to backpack and from backpack to equipment slot
        /// </summary>
        public void HandleInventoryRequestMoveMessage(InventoryRequestMoveMessage request)
        {
            // TODO Normal inventory movement does not require setting of inv loc msg! Just Tick. /fasbat
            Item item = GetItem(request.ItemID);
            if (item == null)
                return;
            // Request to equip item from backpack
            if (request.Location.EquipmentSlot != 0 && request.Location.EquipmentSlot != (int)EquipmentSlotId.Stash)
            {
                var sourceGrid = (item.InvLoc.EquipmentSlot == 0 ? _inventoryGrid :
                    item.InvLoc.EquipmentSlot == (int)EquipmentSlotId.Stash ? _stashGrid : null);

                System.Diagnostics.Debug.Assert((sourceGrid != null && sourceGrid.Contains(request.ItemID)) || _equipment.IsItemEquipped(request.ItemID), "Request to equip unknown item");

                int targetEquipSlot = request.Location.EquipmentSlot;

                if (IsValidEquipmentRequest(item, targetEquipSlot))
                {
                    if (targetEquipSlot == 25 || //Правое кольцо
                        targetEquipSlot == 26 || //Левое кольцо
                        targetEquipSlot == 22 || //Основное оружие
                        targetEquipSlot == 23 //Второстепенное оружие
                        )
                    {
                        Logger.Debug("Экипировка наёмника");
                        Item oldEquipItem = _equipment.GetEquipment(targetEquipSlot);
                        if (oldEquipItem == null)
                        {
                            // determine if item is in backpack or switching item from position with target originally empty
                            if (sourceGrid != null)
                                sourceGrid.RemoveItem(item);
                            else
                                _equipment.UnequipItem(item);

                            _equipment.EquipItem(item, targetEquipSlot);

                            AcceptMoveRequest(item);
                        }
                        else
                        {
                            // check if item is already equipped at another equipmentslot
                            if (_equipment.IsItemEquipped(item))
                            {
                                // switch both items
                                if (!IsValidEquipmentRequest(oldEquipItem, item.EquipmentSlot))
                                    return;

                                int oldEquipmentSlot = _equipment.UnequipItem(item);
                                _equipment.EquipItem(item, targetEquipSlot);
                                _equipment.EquipItem(oldEquipItem, oldEquipmentSlot);

                            }
                            else
                            {
                                // Get original location
                                int x = item.InventoryLocation.X;
                                int y = item.InventoryLocation.Y;
                                // equip item and place other item in the backpack
                                sourceGrid.RemoveItem(item);
                                _equipment.UnequipItem(oldEquipItem);
                                sourceGrid.AddItem(oldEquipItem, y, x);
                                _equipment.EquipItem(item, targetEquipSlot);
                            }
                            AcceptMoveRequest(item);
                            AcceptMoveRequest(oldEquipItem);
                        }

                    }
                    else
                    {
                        Item oldEquipItem = _equipment.GetEquipment(targetEquipSlot);

                        // check if equipment slot is empty
                        if (oldEquipItem == null)
                        {
                            // determine if item is in backpack or switching item from position with target originally empty
                            if (sourceGrid != null)
                                sourceGrid.RemoveItem(item);
                            else
                                _equipment.UnequipItem(item);

                            _equipment.EquipItem(item, targetEquipSlot);

                            AcceptMoveRequest(item);
                        }
                        else
                        {
                            // check if item is already equipped at another equipmentslot
                            if (_equipment.IsItemEquipped(item))
                            {
                                // switch both items
                                if (!IsValidEquipmentRequest(oldEquipItem, item.EquipmentSlot))
                                    return;

                                int oldEquipmentSlot = _equipment.UnequipItem(item);
                                _equipment.EquipItem(item, targetEquipSlot);
                                _equipment.EquipItem(oldEquipItem, oldEquipmentSlot);

                            }
                            else
                            {
                                // Get original location
                                int x = item.InventoryLocation.X;
                                int y = item.InventoryLocation.Y;
                                // equip item and place other item in the backpack
                                sourceGrid.RemoveItem(item);
                                _equipment.UnequipItem(oldEquipItem);
                                sourceGrid.AddItem(oldEquipItem, y, x);
                                _equipment.EquipItem(item, targetEquipSlot);
                            }
                            AcceptMoveRequest(item);
                            AcceptMoveRequest(oldEquipItem);
                        }
                    }

                    SendVisualInventory(this._owner);


                }
            }
            // Request to move an item (from backpack or equipmentslot)
            else
            {
                if (request.Location.EquipmentSlot == 0)
                {
                    // check if not unsocketting rune
                    for (int i = 0; i < _skillSocketRunes.Length; i++)
                    {
                        if (_skillSocketRunes[i] == request.ItemID)
                        {
                            if (_inventoryGrid.FreeSpace(item, request.Location.Row, request.Location.Column))
                            {
                                RemoveRune(i);
                                _inventoryGrid.AddItem(item, request.Location.Row, request.Location.Column);
                                if (item.InvLoc.EquipmentSlot != request.Location.EquipmentSlot)
                                    AcceptMoveRequest(item);
                            }
                            return;
                        }
                    }
                }

                var destGrid = (request.Location.EquipmentSlot == 0 ? _inventoryGrid : _stashGrid);

                if (destGrid.FreeSpace(item, request.Location.Row, request.Location.Column))
                {
                    if (_equipment.IsItemEquipped(item))
                    {
                        _equipment.UnequipItem(item); // Unequip the item
                        SendVisualInventory(this._owner);
                    }
                    else
                    {
                        var sourceGrid = (item.InvLoc.EquipmentSlot == 0 ? _inventoryGrid : _stashGrid);
                        sourceGrid.RemoveItem(item);
                    }
                    destGrid.AddItem(item, request.Location.Row, request.Location.Column);
                    if (item.InvLoc.EquipmentSlot != request.Location.EquipmentSlot)
                        AcceptMoveRequest(item);
                }
                else
                {
                    var sourceGrid = (item.InvLoc.EquipmentSlot == 0 ? _inventoryGrid : _stashGrid);
                    var SizeofSourceItem = sourceGrid.GetItemSize(item);
                    bool UpScale = false;
                    Item DestItem = destGrid.GetItemByLoc(request.Location.Row, request.Location.Column);
                    if (DestItem == null)
                    {
                        //Пытаемся найти 2-этажный предмет
                        var TestItem = destGrid.GetItemByLoc(request.Location.Row - 1, request.Location.Column);

                        var TestSize = sourceGrid.GetItemSize(TestItem);
                        if (TestSize.Y == 2)
                        {
                            DestItem = TestItem;
                            UpScale = true;
                        }
                    }
                    var SizeofDestItem = sourceGrid.GetItemSize(DestItem);

                    bool CanTransfer = true;
                    if (SizeofSourceItem.Y != SizeofDestItem.Y)
                    {
                        if (destGrid.GetItemByLoc(request.Location.Row + 1, request.Location.Column) == null & (request.Location.Row + 1) < 6)
                            CanTransfer = true;
                        else
                            CanTransfer = false;
                        if (destGrid.GetItemByLoc(request.Location.Row + 1, request.Location.Column) != DestItem & destGrid.GetItemByLoc(request.Location.Row + 1, request.Location.Column) != null)
                            CanTransfer = false;
                        /*
                        if (destGrid.GetItemByLoc(request.Location.Row + 1, request.Location.Column) != DestItem)
                            CanTransfer = false;
                        if (destGrid.GetItemByLoc(request.Location.Row + 1, request.Location.Column) == null)
                            CanTransfer = true;
                        */
                        if (SizeofDestItem.Y == 2 & (item.InvLoc.Row + 1) > 5 & UpScale == false)
                            CanTransfer = false;
                        if (destGrid.GetItemByLoc(item.InvLoc.Row + 1, item.InvLoc.Column) != item & SizeofDestItem.Y == 2 & destGrid.GetItemByLoc(item.InvLoc.Row + 1, item.InvLoc.Column) != null)
                            CanTransfer = false;
                        if (UpScale & destGrid.GetItemByLoc(item.InvLoc.Row - 1, item.InvLoc.Column) != item & SizeofDestItem.Y == 2 & destGrid.GetItemByLoc(item.InvLoc.Row - 1, item.InvLoc.Column) != null)
                            CanTransfer = false;
                    }

                    if (DestItem != null & CanTransfer == true)
                    {
                        if (item.ActorSNO.Id == DestItem.ActorSNO.Id & DestItem.ItemDefinition.MaxStackAmount > 1)
                        {
                            Logger.Debug("Тестовое объедение предмета - {0}", item.DynamicID);
                            if (DestItem.Attributes[GameAttribute.ItemStackQuantityLo] + item.Attributes[GameAttribute.ItemStackQuantityLo] > DestItem.ItemDefinition.MaxStackAmount)
                            {
                                int LastCount = (DestItem.Attributes[GameAttribute.ItemStackQuantityLo] + item.Attributes[GameAttribute.ItemStackQuantityLo]) - DestItem.ItemDefinition.MaxStackAmount;
                                DestItem.Attributes[GameAttribute.ItemStackQuantityLo] = DestItem.ItemDefinition.MaxStackAmount;
                                DestItem.Attributes.BroadcastChangedIfRevealed();
                                item.Attributes[GameAttribute.ItemStackQuantityLo] = LastCount;
                                item.Attributes.BroadcastChangedIfRevealed();
                            }
                            else
                            {
                                DestItem.Attributes[GameAttribute.ItemStackQuantityLo] = DestItem.Attributes[GameAttribute.ItemStackQuantityLo] + item.Attributes[GameAttribute.ItemStackQuantityLo];
                                DestItem.Attributes.BroadcastChangedIfRevealed();
                                DestroyInventoryItem(item);
                            }
                        }
                        else
                        {
                            Logger.Debug("Тестовый перенос предмета на предмет. Перемещаемый предмет - {0}, замещяющий - {1}", item.DynamicID, DestItem.DynamicID);
                            int baseX = item.InventoryLocation.X;
                            int baseY = item.InventoryLocation.Y;
                            if (UpScale)
                                baseY -= 1;
                            sourceGrid.RemoveItem(item);
                            destGrid.RemoveItem(DestItem);
                            sourceGrid.AddItem(DestItem, baseY, baseX);
                            destGrid.AddItem(item, request.Location.Row, request.Location.Column);

                        }
                        SendVisualInventory(_owner);
                        RefreshInventoryToClient();
                    }
                    else
                    {
                        Logger.Debug("Тестовый перенос предмета на предмет. Перемещаемый предмет - {0}, замещающий предмет не найден/Недостаточно места", item.DynamicID);
                    }

                }
            }
        }

        /// <summary>
        /// Handles a request to move an item from stash the inventory and back
        /// </summary>
        public void HandleInventoryRequestQuickMoveMessage(InventoryRequestQuickMoveMessage request)
        {
            Item item = GetItem(request.ItemID);
            if (item == null || (request.DestEquipmentSlot != (int)EquipmentSlotId.Stash && request.DestEquipmentSlot != (int)EquipmentSlotId.Inventory))
                return;
            // Identify source and destination grids
            var destinationGrid = request.DestEquipmentSlot == 0 ? _inventoryGrid : _stashGrid;
            var sourceGrid = request.DestEquipmentSlot == 0 ? _stashGrid : _inventoryGrid;

            if (destinationGrid.HasFreeSpace(request.DestRowStart, request.DestRowEnd, item))
            {
                sourceGrid.RemoveItem(item);
                destinationGrid.AddItem(request.DestRowStart, request.DestRowEnd, item);
            }
        }

        /// <summary>
        /// Checks if Item can be equipped at that slot. Handels equipment for Two-Handed-Weapons.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="equipmentSlot"></param>
        /// <returns></returns>
        private bool IsValidEquipmentRequest(Item item, int equipmentSlot)
        {
            ItemTypeTable type = item.ItemType;

            if (equipmentSlot == (int)EquipmentSlotId.Main_Hand)
            {
                // useful for 1hand + shield switching, this is to avoid shield to be go to main hand
                if (!Item.IsWeapon(type))
                    return false;

                if (Item.Is2H(type))
                {
                    Item itemOffHand = _equipment.GetEquipment(EquipmentSlotId.Off_Hand);
                    if (itemOffHand != null)
                    {
                        _equipment.UnequipItem(itemOffHand);
                        if (!_inventoryGrid.AddItem(itemOffHand))
                        {
                            // unequip failed, put back
                            _equipment.EquipItem(itemOffHand, (int)EquipmentSlotId.Off_Hand);
                            return false;
                        }
                        AcceptMoveRequest(itemOffHand);
                    }
                }
            }
            else if (equipmentSlot == (int)EquipmentSlotId.Off_Hand)
            {
                Item itemMainHand = _equipment.GetEquipment(EquipmentSlotId.Main_Hand);
                if (Item.Is2H(type))
                {
                    //remove object first to make room for possible unequiped item
                    _inventoryGrid.RemoveItem(item);

                    if (itemMainHand != null)
                    {
                        _equipment.UnequipItem(itemMainHand);
                        _inventoryGrid.AddItem(itemMainHand);
                        AcceptMoveRequest(itemMainHand);
                    }

                    _equipment.EquipItem(item, (int)EquipmentSlotId.Main_Hand);
                    AcceptMoveRequest(item);

                    SendVisualInventory(this._owner);
                    // All equipment commands are executed. the original EquipmentRequest is invalid at this moment
                    return false;
                }

                if (itemMainHand != null)
                {
                    if (Item.Is2H(itemMainHand.ItemType))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Transfers an amount from one stack to a free space
        /// </summary>
        public void OnInventorySplitStackMessage(InventorySplitStackMessage msg)
        {
            Item itemFrom = GetItem((uint)msg.FromID);
            itemFrom.Attributes[GameAttribute.ItemStackQuantityLo] -= (int)msg.Amount;
            itemFrom.Attributes.SendChangedMessage(_owner.InGameClient);

            Item item = ItemGenerator.CreateItem(_owner, itemFrom.ItemDefinition);
            item.Attributes[GameAttribute.ItemStackQuantityLo] = (int)msg.Amount;

            InventoryGrid targetGrid = (msg.InvLoc.EquipmentSlot == (int)EquipmentSlotId.Stash) ? _stashGrid : _inventoryGrid;
            targetGrid.AddItem(item, msg.InvLoc.Row, msg.InvLoc.Column);
        }

        /// <summary>
        /// Transfers an amount from one stack to another
        /// </summary>
        public void OnInventoryStackTransferMessage(InventoryStackTransferMessage msg)
        {
            Item itemFrom = GetItem(msg.FromID);
            Item itemTo = GetItem(msg.ToID);

            itemFrom.Attributes[GameAttribute.ItemStackQuantityLo] -= (int)msg.Amount;
            itemTo.Attributes[GameAttribute.ItemStackQuantityLo] += (int)msg.Amount;

            itemFrom.Attributes.SendChangedMessage(_owner.InGameClient);
            itemTo.Attributes.SendChangedMessage(_owner.InGameClient);
        }
        private List<DBInventory> _dbInventoriesToDelete = new List<DBInventory>();
        private void OnInventoryDropItemMessage(InventoryDropItemMessage msg)
        {
            var item = GetItem(msg.ItemID);
            if (item == null)
                return; // TODO: Throw smthg? /fasbat

            if (_equipment.IsItemEquipped(item))
            {
                _equipment.UnequipItem(item);
                SendVisualInventory(this._owner);
            }
            else
            {
                var sourceGrid = (item.InvLoc.EquipmentSlot == 0 ? _inventoryGrid : _stashGrid);
                sourceGrid.RemoveItem(item);
            }

            item.CurrentState = ItemState.Dropping;
            item.Unreveal(_owner);
            item.SetNewWorld(_owner.World);
            item.Drop(null, _owner.Position);
            if (item.DBInventory != null)
                _dbInventoriesToDelete.Add(item.DBInventory);
            item.DBInventory = null;
            item.CurrentState = ItemState.Normal;
            AcceptMoveRequest(item);
        }

        public void Consume(GameClient client, GameMessage message)
        {
            if (message is InventoryRequestMoveMessage) HandleInventoryRequestMoveMessage(message as InventoryRequestMoveMessage);
            else if (message is InventoryRequestQuickMoveMessage) HandleInventoryRequestQuickMoveMessage(message as InventoryRequestQuickMoveMessage);
            else if (message is InventorySplitStackMessage) OnInventorySplitStackMessage(message as InventorySplitStackMessage);
            else if (message is InventoryStackTransferMessage) OnInventoryStackTransferMessage(message as InventoryStackTransferMessage);
            else if (message is InventoryDropItemMessage) OnInventoryDropItemMessage(message as InventoryDropItemMessage);
            else if (message is InventoryRequestUseMessage) OnInventoryRequestUseMessage(message as InventoryRequestUseMessage);
            else if (message is RequestBuySharedStashSlotsMessage) OnBuySharedStashSlots(message as RequestBuySharedStashSlotsMessage);
            else if (message is InventoryRequestUseMessage) OnInventoryRequestUseMessage(message as InventoryRequestUseMessage);
            else if (message is InventoryIdentifyItemMessage) OnInventoryIdentifyItemMessage(message as InventoryIdentifyItemMessage);

            _owner.SetAttributesByItems();
            _owner.Attributes.BroadcastChangedIfRevealed();
        }

        private void OnInventoryIdentifyItemMessage(InventoryIdentifyItemMessage msg)
        {
            var item = GetItem(msg.ItemID);
            if (item == null)
                return;

            Logger.Warn("Identifying items not implemented yet");
        }

        private void OnBuySharedStashSlots(RequestBuySharedStashSlotsMessage requestBuySharedStashSlotsMessage)
        {
            int amount = 10000;

            if (_stashGrid.Rows % 10 == 0)
            {
                if (_stashGrid.Rows / 10 - 1 >= _stashBuyValue.Length)
                    return;
                amount = _stashBuyValue[_stashGrid.Rows / 10 - 1];
            }
            if (GetGoldAmount() >= amount)
            {
                RemoveGoldAmount(amount);
                _owner.Attributes[GameAttribute.Shared_Stash_Slots] += 14;
                _owner.Attributes.BroadcastChangedIfRevealed();
                _stashGrid.ResizeGrid(_owner.Attributes[GameAttribute.Shared_Stash_Slots] / 7, 7);
            }
        }

        // TODO: The inventory's gold item should not be created here. /komiga
        public void PickUpGold(uint itemID)
        {
            Item collectedItem = _owner.World.GetItem(itemID);
            AddGoldAmount(collectedItem.Attributes[GameAttribute.Gold]);
        }

        private void OnInventoryRequestUseMessage(InventoryRequestUseMessage inventoryRequestUseMessage)
        {
            uint targetItemId = inventoryRequestUseMessage.UsedOnItem;
            uint usedItemId = inventoryRequestUseMessage.UsedItem;
            int actionId = inventoryRequestUseMessage.Field1;
            Item usedItem = GetItem(usedItemId);
            Item targetItem = GetItem(targetItemId);

            usedItem.OnRequestUse(_owner, targetItem, actionId, inventoryRequestUseMessage.Location);
        }

        public void DestroyInventoryItem(Item item)
        {
            if (_equipment.IsItemEquipped(item))
            {
                _equipment.UnequipItem(item);
                SendVisualInventory(_owner);
            }
            else
            {
                _inventoryGrid.RemoveItem(item);
            }

            item.Destroy();
            _destroyedItems.Add(item);
        }

        private List<Item> _destroyedItems = new List<Item>();

        public bool Reveal(Player player)
        {
            _equipment.Reveal(player);
            if (player == _owner)
            {
                _inventoryGrid.Reveal(player);
                _stashGrid.Reveal(player);
            }
            return true;
        }

        public bool Unreveal(Player player)
        {
            _equipment.Unreveal(player);
            if (player == _owner)
            {
                _inventoryGrid.Unreveal(player);
                _stashGrid.Unreveal(player);
            }

            return true;
        }

        public Item GetItem(uint itemId)
        {
            Item result;
            if (!_inventoryGrid.Items.TryGetValue(itemId, out result) &&
                !_stashGrid.Items.TryGetValue(itemId, out result) &&
                !_equipment.Items.TryGetValue(itemId, out result))
            {
                return null;
            }
            return result;
        }

        /// <summary>
        /// Returns rune in skill's socket
        /// </summary>
        /// <param name="skillIndex"></param>
        /// <returns></returns>
        public Item GetRune(int skillIndex)
        {
            if ((skillIndex < 0) || (skillIndex > 5))
            {
                return null;
            }
            if (_skillSocketRunes[skillIndex] == 0)
            {
                return null;
            }
            return _equipment.GetItem(_skillSocketRunes[skillIndex]);
        }

        /// <summary>
        /// Visually adds rune to skill (move from backpack to runes' slot)
        /// </summary>
        /// <param name="rune"></param>
        /// <param name="powerSNOId"></param>
        /// <param name="skillIndex"></param>
        public void SetRune(Item rune, int powerSNOId, int skillIndex)
        {
            if ((skillIndex < 0) || (skillIndex > 5))
            {
                return;
            }
            if (rune == null)
            {
                _skillSocketRunes[skillIndex] = 0;
                return;
            }
            if (_inventoryGrid.Items.ContainsKey(rune.DynamicID))
            {
                _inventoryGrid.RemoveItem(rune);
            }
            else
            {
                // unattuned rune changes to attuned w/o getting into inventory
                rune.World.Leave(rune);
                rune.Reveal(_owner);
            }
            _equipment.Items.Add(rune.DynamicID, rune);
            _skillSocketRunes[skillIndex] = rune.DynamicID;
            // will set only one of these to rank
            _owner.Attributes[GameAttribute.Rune_A, powerSNOId] = rune.Attributes[GameAttribute.Rune_A];
            _owner.Attributes[GameAttribute.Rune_B, powerSNOId] = rune.Attributes[GameAttribute.Rune_B];
            _owner.Attributes[GameAttribute.Rune_C, powerSNOId] = rune.Attributes[GameAttribute.Rune_C];
            _owner.Attributes[GameAttribute.Rune_D, powerSNOId] = rune.Attributes[GameAttribute.Rune_D];
            _owner.Attributes[GameAttribute.Rune_E, powerSNOId] = rune.Attributes[GameAttribute.Rune_E];
            // position of rune is read from mpq as INDEX of skill in skill kit - loaded in helper /xsochor
            rune.SetInventoryLocation(15, RuneHelper.GetRuneIndexForPower(powerSNOId), 0);
        }

        /// <summary>
        /// Visually removes rune from skill. Also removes effect of that rune
        /// </summary>
        /// <param name="skillIndex"></param>
        /// <returns></returns>
        public Item RemoveRune(int skillIndex)
        {
            if ((skillIndex < 0) || (skillIndex > 5))
            {
                return null;
            }
            Item rune = GetRune(skillIndex);
            if (rune != null)
            {
                _equipment.Items.Remove(rune.DynamicID);
            }
            int powerSNOId = _owner.SkillSet.ActiveSkills[skillIndex].snoSkill;
            _skillSocketRunes[skillIndex] = 0;
            _owner.Attributes[GameAttribute.Rune_A, powerSNOId] = 0;
            _owner.Attributes[GameAttribute.Rune_B, powerSNOId] = 0;
            _owner.Attributes[GameAttribute.Rune_C, powerSNOId] = 0;
            _owner.Attributes[GameAttribute.Rune_D, powerSNOId] = 0;
            _owner.Attributes[GameAttribute.Rune_E, powerSNOId] = 0;
            return rune;
        }

        public void AddGoldAmount(int amount)
        {
            int maxGoldValue = 2147483647; // Max int value [Necrosummon]

            if (_inventoryGold.Attributes[GameAttribute.Gold] == maxGoldValue)
                amount = 0;
            else
            {
                _inventoryGold.Attributes[GameAttribute.Gold] += amount;
                _inventoryGold.Attributes[GameAttribute.ItemStackQuantityLo] = _inventoryGold.Attributes[GameAttribute.Gold];
                _inventoryGold.Attributes.SendChangedMessage(_owner.InGameClient);
            }
            _owner.Toon.DBToon.DBGameAccount.GoldCollected += (uint)amount; // Add gold amount to Gold Collected Account Profile [Necrosummon]
        }

        public void RemoveGoldAmount(int amount)
        {
            _inventoryGold.Attributes[GameAttribute.Gold] -= amount;
            _inventoryGold.Attributes[GameAttribute.ItemStackQuantityLo] = _inventoryGold.Attributes[GameAttribute.Gold];
            _inventoryGold.Attributes.SendChangedMessage(_owner.InGameClient);
        }

        public int GetGoldAmount()
        {
            return _inventoryGold.Attributes[GameAttribute.Gold];
        }

        private static IEnumerable<DBInventory> _dbInventories = DBSessions.AccountSession.Query<DBInventory>().Fetch(inv => inv.DBItemInstance);


        public void LoadFromDB()
        {
            //load everything and make a switch on slot_id
            Item item = null;
            int goldAmount = _owner.Toon.GameAccount.DBGameAccount.Gold;
            int goldHCAmount = _owner.Toon.GameAccount.DBGameAccount.GoldHC;
            // Clear already present items
            // LoadFromDB is called every time World is changed, even entering a dungeon
            _stashGrid.Clear();
            _inventoryGrid.Clear();



            // first of all load stash size

            var slots = this._owner.Toon.GameAccount.DBGameAccount.StashSize;
            var slotsHC = this._owner.Toon.GameAccount.DBGameAccount.StashSizeHC;

            if (slots > 0 && !_owner.Toon.DBToon.Hardcore)
            {
                _owner.Attributes[GameAttribute.Shared_Stash_Slots] = slots;
                _owner.Attributes.BroadcastChangedIfRevealed();
                // To be applied before loading items, to have all the space needed
                _stashGrid.ResizeGrid(_owner.Attributes[GameAttribute.Shared_Stash_Slots] / 7, 7);
            }
            else if (slotsHC > 0 && _owner.Toon.DBToon.Hardcore)
            {
                _owner.Attributes[GameAttribute.Shared_Stash_Slots] = slotsHC;
                _owner.Attributes.BroadcastChangedIfRevealed();
                // To be applied before loading items, to have all the space needed
                _stashGrid.ResizeGrid(_owner.Attributes[GameAttribute.Shared_Stash_Slots] / 7, 7);
            }

            if (!_owner.Toon.DBToon.Hardcore)
            {
                var stashInventoryItems =
                    _dbInventories.Where(
                        dbi =>
                        dbi.DBGameAccount.Id == _owner.Toon.GameAccount.PersistentID && dbi.DBToon == null &&
                        dbi.DBItemInstance != null && !dbi.Hardcore).ToList();

                foreach (var inv in stashInventoryItems)
                {
                    var slot = inv.EquipmentSlot;

                    if (slot == (int)EquipmentSlotId.Stash)
                    {
                        // load stash
                        item = ItemGenerator.LoadFromDBInstance(_owner, inv.DBItemInstance);
                        item.DBInventory = inv;
                        item.DBItemInstance = inv.DBItemInstance;
                        this._stashGrid.AddItem(item, inv.LocationY, inv.LocationX);
                    }
                }
            }
            else if (_owner.Toon.DBToon.Hardcore)
            {
                var stashInventoryItemsHC =
                    _dbInventories.Where(
                     dbi =>
                     dbi.DBGameAccount.Id == _owner.Toon.GameAccount.PersistentID && dbi.DBToon == null &&
                     dbi.DBItemInstance != null && dbi.Hardcore).ToList();

                foreach (var inv in stashInventoryItemsHC)
                {
                    var slot = inv.EquipmentSlot;

                    if (slot == (int)EquipmentSlotId.Stash)
                    {
                        // load stash
                        item = ItemGenerator.LoadFromDBInstance(_owner, inv.DBItemInstance);
                        item.DBInventory = inv;
                        item.DBItemInstance = inv.DBItemInstance;
                        this._stashGrid.AddItem(item, inv.LocationY, inv.LocationX);
                    }
                }
            }

            // next read all items
            var allInventoryItems = _dbInventories.Where(
                    dbi =>
                    dbi.DBToon != null && dbi.DBToon.Id == _owner.Toon.PersistentID && dbi.DBItemInstance != null).ToList();


            foreach (var inv in allInventoryItems)
            {
                var slot = inv.EquipmentSlot;
                if (slot >= (int)EquipmentSlotId.Inventory && slot <= (int)EquipmentSlotId.Neck)
                {
                    item = ItemGenerator.LoadFromDBInstance(_owner, inv.DBItemInstance);
                    item.DBInventory = inv;
                    item.DBItemInstance = inv.DBItemInstance;
                    if (slot == (int)EquipmentSlotId.Inventory)
                    {
                        this._inventoryGrid.AddItem(item, inv.LocationY, inv.LocationX);
                    }
                    else
                    {
                        _equipment.EquipItem(item, (int)slot);
                    }
                }

                if (slot >= (int)EquipmentSlotId.PetRightHand && slot <= (int)EquipmentSlotId.PetLeftFinger)
                {
                    item = ItemGenerator.LoadFromDBInstance(_owner, inv.DBItemInstance);
                    item.DBInventory = inv;
                    item.DBItemInstance = inv.DBItemInstance;
                    if (slot == (int)EquipmentSlotId.Inventory)
                    {
                        this._inventoryGrid.AddItem(item, inv.LocationY, inv.LocationX);
                    }
                    else
                    {
                        _equipment.EquipItem(item, (int)slot);
                    }
                }
            }


            if (!_owner.Toon.DBToon.Hardcore)
            {
                this._inventoryGold = ItemGenerator.CreateGold(this._owner, goldAmount);
                this._inventoryGold.Attributes[GameAttribute.ItemStackQuantityLo] = goldAmount; // This is the attribute that makes the gold visible in game
                this._inventoryGold.Owner = _owner;
                this._inventoryGold.SetInventoryLocation((int)EquipmentSlotId.Gold, 0, 0);
                this.Loaded = true;
            }
            else
            {
                this._inventoryGold = ItemGenerator.CreateGold(this._owner, goldHCAmount);
                this._inventoryGold.Attributes[GameAttribute.ItemStackQuantityLo] = goldHCAmount; // This is the attribute that makes the gold visible in game
                this._inventoryGold.Owner = _owner;
                this._inventoryGold.SetInventoryLocation((int)EquipmentSlotId.Gold, 0, 0);
                this.Loaded = true;
            }
        }

        public void RefreshInventoryToClient()
        {
            var itemsToUpdate = new List<Item>();
            itemsToUpdate.AddRange(this._inventoryGrid.Items.Values);
            itemsToUpdate.AddRange(this._stashGrid.Items.Values);
            itemsToUpdate.Add(this._inventoryGold);

            foreach (var itm in itemsToUpdate)
            {
                if (itm.Owner is GS.Players.Player)
                {
                    var player = (itm.Owner as GS.Players.Player);
                    if (!itm.Reveal(player))
                    {
                        player.InGameClient.SendMessage(itm.ACDInventoryPositionMessage);
                    }
                }
            }

        }

        // TODO: change saving at the world OnLeave to saving at every inventory change, //~weltmeyer:done:without delete and insert 
        public void SaveToDB()
        {
            var dbToon = DBSessions.AccountSession.Get<DBToon>(this._owner.Toon.PersistentID);
            var dbGameAccount = DBSessions.AccountSession.Get<DBGameAccount>(this._owner.Toon.GameAccount.PersistentID);


            foreach (var dbInventory in _dbInventoriesToDelete)
            {
                DBSessions.AccountSession.Delete(dbInventory);
            }

            foreach (var inv in _destroyedItems.Where(inv => inv.DBInventory != null))
            {
                DBSessions.AccountSession.Delete(inv.DBInventory);
            }
            DBSessions.AccountSession.Flush();

            _destroyedItems.Clear();
            _dbInventoriesToDelete.Clear();
            // save equipment
            for (int i = 1; i <= 13; i++) // from Helm = 1 to Neck = 13 in EquipmentSlotId
            {
                SaveItemToDB(dbGameAccount, dbToon, (EquipmentSlotId)i, _equipment.GetEquipment((EquipmentSlotId)i));
            }

            // save inventory
            foreach (Item itm in _inventoryGrid.Items.Values)
            {
                SaveItemToDB(dbGameAccount, dbToon, EquipmentSlotId.Inventory, itm);
            }

            // save stash
            if (!_owner.Toon.DBToon.Hardcore)
                dbGameAccount.StashSize = _owner.Attributes[GameAttribute.Shared_Stash_Slots];
            else
                dbGameAccount.StashSizeHC = _owner.Attributes[GameAttribute.Shared_Stash_Slots];
            foreach (Item itm in _stashGrid.Items.Values)
            {
                SaveItemToDB(dbGameAccount, null, EquipmentSlotId.Stash, itm);
            }

            // save gold
            if (!_owner.Toon.DBToon.Hardcore)
                dbGameAccount.Gold = GetGoldAmount();
            else
                dbGameAccount.GoldHC = GetGoldAmount();
            DBSessions.AccountSession.SaveOrUpdate(dbGameAccount);
            DBSessions.AccountSession.Flush();
        }

        private void SaveItemToDB(DBGameAccount dbGameAccount, DBToon dbToon, EquipmentSlotId slotId, Item item)
        {
            if (item == null)
                return;
            if (item.DBInventory == null)
                item.DBInventory = new DBInventory();

            item.DBInventory.DBGameAccount = dbGameAccount;
            item.DBInventory.DBToon = dbToon;
            item.DBInventory.LocationX = item.InventoryLocation.X;
            item.DBInventory.LocationY = item.InventoryLocation.Y;
            item.DBInventory.EquipmentSlot = (int)slotId;

            ItemGenerator.SaveToDB(item);
            item.DBInventory.DBItemInstance = item.DBItemInstance;
            DBSessions.AccountSession.SaveOrUpdate(item.DBInventory);
        }

        #region EqupimentStats
        public float GetItemBonus(GameAttributeF attributeF)
        {
            return this.Loaded ? this.GetEquippedItems().Sum(item => item.Attributes[attributeF]) : 0.0f;
        }

        public int GetItemBonus(GameAttributeI attributeI)
        {
            return this.Loaded ? this.GetEquippedItems().Sum(item => item.Attributes[attributeI]) : 0;
        }

        public float GetItemBonus(GameAttributeF attributeF, int attributeKey)
        {
            return this.Loaded ? this.GetEquippedItems().Sum(item => item.Attributes[attributeF, attributeKey]) : 0.0f;
        }

        public int GetItemBonus(GameAttributeI attributeI, int attributeKey)
        {
            return this.Loaded ? this.GetEquippedItems().Sum(item => item.Attributes[attributeI, attributeKey]) : 0;
        }

        #endregion
    }
}