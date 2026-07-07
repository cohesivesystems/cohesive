namespace Cohesive.AI.Tests.Semantics;

static class TestData
{
    internal static class Edi204ConceptIds
    {
        public const string PartyRole = "party.role";
        public const string PartyShipper = "party.shipper";
        public const string PartyShipTo = "party.ship_to";
        public const string PartyBillTo = "party.bill_to";
        public const string ShipmentReferenceNumber = "shipment.reference_number";
        public const string DatePickupType = "date.pickup.type";
        public const string DatePickupRequested = "date.pickup.requested";
        public const string PartyShipToName = "party.ship_to.name";
        public const string ShipmentWeightValue = "shipment.weight.value";
        public const string ShipmentWeightUom = "shipment.weight.uom";
        public const string ShipmentLocation = "shipment.location";
        public const string ShipmentLocationCity = "shipment.location.city";
        public const string ShipmentLocationState = "shipment.location.state";
        public const string ShipmentLocationMunicipality = "shipment.location.municipality";
    }

    internal static class Edi204Codes
    {
        public const string Shipper = "SH";
        public const string ShipTo = "ST";
        public const string BillTo = "BT";
        public const string Other = "ZZ";
    }

    internal static class Edi204Scopes
    {
        public const string N101 = "N1-01";
        public const string QualifiedN101 = "X12/004010/204/N1/N1-01";
    }
}
