namespace myfoodapp.Hub.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class AddLastSignalStrenght : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.ProductionUnits", "lastSignalStrenghtReceived", c => c.String());
        }
        
        public override void Down()
        {
            DropColumn("dbo.ProductionUnits", "lastSignalStrenghtReceived");
        }
    }
}
