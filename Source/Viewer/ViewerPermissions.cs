using Verse;

namespace Overlord
{
    /// <summary>
    /// Per-colonist permission flags. Defaults come from mod settings,
    /// but the streamer can override per assignment.
    /// </summary>
    public class ViewerPermissions : IExposable
    {
        public bool draft = true;
        public bool move = true;
        public bool attack = true;
        public bool work = true;
        public bool schedule = true;
        public bool outfit = true;
        public bool drugPolicy = true;
        public bool foodPolicy = true;
        public bool area = true;
        public bool equip = true;
        public bool appearance = false;

        public ViewerPermissions()
        {
            ApplyDefaults();
        }

        public void ApplyDefaults()
        {
            var s = OverlordMod.Settings;
            if (s == null) return;
            draft = s.allowDraft;
            move = s.allowMove;
            attack = s.allowAttack;
            work = s.allowWork;
            schedule = s.allowSchedule;
            outfit = s.allowOutfit;
            drugPolicy = s.allowDrugPolicy;
            foodPolicy = s.allowFoodPolicy;
            area = s.allowArea;
            equip = s.allowEquip;
            appearance = s.allowAppearance;
        }

        public bool IsAllowed(string action)
        {
            switch (action)
            {
                case StateProtocol.CmdDraft:
                case StateProtocol.CmdUndraft:
                    return draft;
                case StateProtocol.CmdMove:
                    return move;
                case StateProtocol.CmdAttack:
                    return attack;
                case StateProtocol.CmdSetWork:
                    return work;
                case StateProtocol.CmdSetSchedule:
                    return schedule;
                case StateProtocol.CmdSetOutfit:
                    return outfit;
                case StateProtocol.CmdSetDrugPolicy:
                    return drugPolicy;
                case StateProtocol.CmdSetFoodPolicy:
                    return foodPolicy;
                case StateProtocol.CmdSetArea:
                    return area;
                case StateProtocol.CmdSetAppearance:
                case StateProtocol.CmdDyeApparel:
                    return appearance;
                case StateProtocol.CmdEquip:
                case StateProtocol.CmdDrop:
                case StateProtocol.CmdDropInventory:
                    return equip;
                case StateProtocol.CmdHostileResponse:
                    return draft; // same permission as drafting
                case StateProtocol.CmdConsume:
                    return true; // always allowed
                case StateProtocol.CmdContextMenu:
                case StateProtocol.CmdContextAction:
                case StateProtocol.CmdSocialInteract:
                    return move; // context actions are movement-tier
                case StateProtocol.RequestState:
                case StateProtocol.StateResyncRequest:
                    return true;
                default:
                    return false;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref draft, "draft", true);
            Scribe_Values.Look(ref move, "move", true);
            Scribe_Values.Look(ref attack, "attack", true);
            Scribe_Values.Look(ref work, "work", true);
            Scribe_Values.Look(ref schedule, "schedule", true);
            Scribe_Values.Look(ref outfit, "outfit", true);
            Scribe_Values.Look(ref drugPolicy, "drugPolicy", true);
            Scribe_Values.Look(ref foodPolicy, "foodPolicy", true);
            Scribe_Values.Look(ref area, "area", true);
            Scribe_Values.Look(ref equip, "equip", true);
            Scribe_Values.Look(ref appearance, "appearance", false);
        }
    }
}
