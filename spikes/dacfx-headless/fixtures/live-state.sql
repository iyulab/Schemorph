-- Simulated "current" database state for the spike.
-- The desired-state files diverge from this on purpose:
--   Customers      : desired adds [Email] column           -> expect ALTER (Change)
--   Orders         : absent here, present in desired state -> expect CREATE (Add)
--   LegacyLog      : present here, absent in desired state -> expect DROP (Delete)
--   GetCustomer    : body differs in desired state         -> expect Change
CREATE TABLE dbo.Customers
(
    Id   INT           NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL
);
GO
CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);
GO
CREATE TABLE dbo.LegacyLog
(
    Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LegacyLog PRIMARY KEY,
    Message NVARCHAR(MAX)     NULL
);
GO
CREATE PROCEDURE dbo.GetCustomer
    @Id INT
AS
BEGIN
    SELECT Id, Name FROM dbo.Customers WHERE Id = @Id;
END
GO
