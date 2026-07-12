CREATE TABLE dbo.Customers
(
    Id    INT           NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name  NVARCHAR(100) NOT NULL,
    Email NVARCHAR(256) NULL
);
GO
CREATE INDEX IX_Customers_Name ON dbo.Customers (Name);
GO
