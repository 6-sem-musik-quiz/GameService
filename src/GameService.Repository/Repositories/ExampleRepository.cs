using GameService.Domain.Entities;
using GameService.Repository.Data;
using GameService.Repository.Interfaces;

namespace GameService.Repository.Repositories;

public class ExampleRepository(AppDbContext context) : Repository<Example>(context), IExampleRepository
{
}
