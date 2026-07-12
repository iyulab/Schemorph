CREATE PROCEDURE dbo.GetCustomer
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name, Email FROM dbo.Customers WHERE Id = @Id;
END
GO
