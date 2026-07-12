CREATE TABLE dbo.Orders
(
    Id         INT            NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId INT            NOT NULL,
    PlacedAt   DATETIME2(3)   NOT NULL CONSTRAINT DF_Orders_PlacedAt DEFAULT SYSUTCDATETIME(),
    Total      DECIMAL(18, 2) NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (Id)
);
GO
