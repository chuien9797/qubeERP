SET NOCOUNT ON;
IF DB_ID(N'ProductDb') IS NULL CREATE DATABASE [ProductDb];
IF DB_ID(N'InventoryDb') IS NULL CREATE DATABASE [InventoryDb];
IF DB_ID(N'OrderDb') IS NULL CREATE DATABASE [OrderDb];
GO
IF SUSER_ID(N'qube_product') IS NULL
    CREATE LOGIN [qube_product] WITH PASSWORD = N'$(PRODUCT_DB_PASSWORD)', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [qube_product] WITH PASSWORD = N'$(PRODUCT_DB_PASSWORD)';
IF SUSER_ID(N'qube_inventory') IS NULL
    CREATE LOGIN [qube_inventory] WITH PASSWORD = N'$(INVENTORY_DB_PASSWORD)', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [qube_inventory] WITH PASSWORD = N'$(INVENTORY_DB_PASSWORD)';
IF SUSER_ID(N'qube_order') IS NULL
    CREATE LOGIN [qube_order] WITH PASSWORD = N'$(ORDER_DB_PASSWORD)', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [qube_order] WITH PASSWORD = N'$(ORDER_DB_PASSWORD)';
GO
USE [ProductDb];
IF USER_ID(N'qube_product') IS NULL CREATE USER [qube_product] FOR LOGIN [qube_product];
ALTER ROLE [db_owner] ADD MEMBER [qube_product];
GO
USE [InventoryDb];
IF USER_ID(N'qube_inventory') IS NULL CREATE USER [qube_inventory] FOR LOGIN [qube_inventory];
ALTER ROLE [db_owner] ADD MEMBER [qube_inventory];
GO
USE [OrderDb];
IF USER_ID(N'qube_order') IS NULL CREATE USER [qube_order] FOR LOGIN [qube_order];
ALTER ROLE [db_owner] ADD MEMBER [qube_order];
