import viewModelBase = require("viewmodels/viewModelBase");
import sqlMigration = require("models/database/tasks/sql/sqlMigration");
import fetchSqlDatabaseSchemaCommand = require("commands/database/tasks/fetchSqlDatabaseSchemaCommand");
import migrateSqlDatabaseCommand = require("commands/database/tasks/migrateSqlDatabaseCommand");
import sqlReference = require("models/database/tasks/sql/sqlReference");
import messagePublisher = require("common/messagePublisher");
import rootSqlTable = require("models/database/tasks/sql/rootSqlTable");

//TODO: consider removing 'Please provide 'Database name' in field below, instead of using' - instead automatically extract this from connection string on blur
class importCollectionFromSql extends viewModelBase {
    
    static pageCount = 50; //TODO: set to 100!
    
    spinners = {
        schema: ko.observable<boolean>(false)
    };
    
    model = new sqlMigration();
    
    currentPage = ko.observable<number>(0);
    pageCount: KnockoutComputed<number>;
    currentTables: KnockoutComputed<Array<rootSqlTable>>;
    currentLocationHumane: KnockoutComputed<string>;
    
    inFirstStep = ko.observable<boolean>(true);
    
    databases = ko.observableArray<string>([]); //TODO: fetch this on databases focus
    
    validationGroup: KnockoutValidationGroup;    

    constructor() {
        super();

        this.bindToCurrentInstance("onActionClicked", "setCurrentPage");
        
        this.pageCount = ko.pureComputed(() => Math.ceil(this.model.tables().length / importCollectionFromSql.pageCount) );
        
        this.currentTables = ko.pureComputed(() => {
            const start = this.currentPage() * importCollectionFromSql.pageCount;
            return this.model.tables().slice(start, start + importCollectionFromSql.pageCount);
        });
        
        this.currentLocationHumane = ko.pureComputed(() => {
            const total = this.model.tables().length;
            
            const start = this.currentPage() * importCollectionFromSql.pageCount + 1;
            const end = Math.min(total, start + importCollectionFromSql.pageCount - 1);
            
            return "Tables " + start.toLocaleString() + "-" + end.toLocaleString() + " out of " + total.toLocaleString();
        });
    }
    
    setCurrentPage(page: number) {
        this.currentPage(page);
        //TODO: scroll up
    }
    
    nextStep() {        
        if (!this.isValid(this.model.getValidationGroup())) {
            return false;
        }
        
        const connectionString = this.model.getConnectionString(); 
        
        this.spinners.schema(true);
        
        const schemaRequestDto = {
            Provider: this.model.databaseType(),
            ConnectionString: connectionString
        } as Raven.Server.SqlMigration.Model.SourceSqlDatabase;
        
        new fetchSqlDatabaseSchemaCommand(this.activeDatabase(), schemaRequestDto)
            .execute()
            .done(schema => {
                this.inFirstStep(false);
                this.model.onSchemaUpdated(schema);
                
                this.initSecondStep();
            })
            .always(() => this.spinners.schema(false));
            
        //TODO: finish
    }
    
    private initSecondStep() {
        /* TODO:
        <div class="hover-preview" style="width: 100px; height: 5px ;background-color: red; position: absolute; top: 0px; left: 0">AAAA</div>
        
        const hover = $(".hover-preview");
        $("#js-second-step").on("mouseenter mouseleave", ".prop", (event) => {
            const top = $(event.target).position().top;
            
            hover.css('top',  top + "px");
            console.log(event);
        });*/
    }
    
    migrate() {
        const dto = this.model.toDto();
        new migrateSqlDatabaseCommand(this.activeDatabase(), dto)
            .execute()
            .done(() => messagePublisher.reportSuccess("OK!"));
        //TODO: operation id + watch
    }
    
    onActionClicked(reference: sqlReference, action: sqlMigrationAction) {
        if (action === "embed" && reference.action() !== "embed") {
            const tableToEmbed = this.model.findRootTable(reference.targetTable.tableSchema, reference.targetTable.tableName);
            reference.effectiveInnerTable(tableToEmbed.cloneForEmbed());
        }
        
        if (action === "link" && reference.action() !== "link") {
            const tableToLink = this.model.findRootTable(reference.targetTable.tableSchema, reference.targetTable.tableName);
            // no need to clone in this case
            reference.effectiveLinkTable(tableToLink);
        }
        
        reference.action(action);
        
        if (action !== "embed") {
            reference.effectiveInnerTable(null);
        }
        
        if (action !== "link") {
            reference.effectiveLinkTable(null);
        }
    }
}

export = importCollectionFromSql; 
