CREATE FUNCTION dbo.fn_OrderTotal (@CustomerId INT)
RETURNS DECIMAL(18, 2)
AS
BEGIN
    RETURN (SELECT COALESCE(SUM(Total), 0) FROM dbo.Orders WHERE CustomerId = @CustomerId);
END
GO
