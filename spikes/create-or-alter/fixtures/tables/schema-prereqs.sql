-- Structural objects the programmable set references.
-- Loaded into the TSqlModel so references resolve; NOT applied by this spike
-- (structural changes are strategy 1's job).
CREATE TABLE dbo.Customers
(
    Id    INT           NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name  NVARCHAR(100) NOT NULL,
    Email NVARCHAR(256) NULL
);
GO
CREATE TABLE dbo.Orders
(
    Id         INT            NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId INT            NOT NULL,
    PlacedAt   DATETIME2(3)   NOT NULL,
    Total      DECIMAL(18, 2) NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id)
);
GO
